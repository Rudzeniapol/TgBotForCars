using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TGBotForCars;

class Program
{
    private static ConcurrentDictionary<long, UserSession> _sessions = new();
    private static string _botToken = "8562705131:AAGuhDVk9Tcb_XZSqqHiSh0NIsAFPVbYmBw"; 

    static async Task Main(string[] args)
    {
        StorageService.Initialize();
        
        var botClient = new TelegramBotClient(_botToken);
        using CancellationTokenSource cts = new();

        ReceiverOptions receiverOptions = new() { AllowedUpdates = Array.Empty<UpdateType>() };

        // В новой версии параметры называются updateHandler и errorHandler
        botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: cts.Token
        );

        Console.WriteLine("Bot is running. Press Enter to shut it down.");
        Console.ReadLine();
        cts.Cancel();
    }

    static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { } message) return;
        
        var chatId = message.Chat.Id;
        var messageText = message.Text;
        var username = message.Chat.Username ?? "Unknown";

        if (!_sessions.ContainsKey(chatId)) _sessions[chatId] = new UserSession();
        var session = _sessions[chatId];

        try
        {
            if (messageText == "/start" || messageText == "/reset")
            {
                session.State = UserState.Idle;
                session.AnglePhotoPaths.Clear();
                // SendTextMessageAsync -> SendMessage
                await botClient.SendMessage(chatId, 
                    "Привет! \n" +
                    "Команды:\n" +
                    "/add - Добавить отчет по машине\n" +
                    "/report - (Для менеджера) Получить Excel отчет", cancellationToken: cancellationToken);
                return;
            }

            switch (session.State)
            {
                case UserState.Idle:
                    if (messageText == "/add")
                    {
                        session.State = UserState.AwaitingPlate;
                        await botClient.SendMessage(chatId, "Введите номер машины:", cancellationToken: cancellationToken);
                    }
                    else if (messageText == "/report")
                    {
                        session.State = UserState.ManagerAwaitingPlate;
                        await botClient.SendMessage(chatId, "Введите номер машины для формирования отчета:", cancellationToken: cancellationToken);
                    }
                    break;

                case UserState.AwaitingPlate:
                    if (string.IsNullOrWhiteSpace(messageText)) return;
                    session.CarPlate = messageText.ToUpper();
                    session.State = UserState.AwaitingGeneralPhoto;
                    await botClient.SendMessage(chatId, "Номер принят. Отправьте общее фото машины с номером.", cancellationToken: cancellationToken);
                    break;

                case UserState.AwaitingGeneralPhoto:
                    if (message.Photo != null)
                    {
                        var photoId = message.Photo.Last().FileId;
                        session.GeneralPhotoPath = await StorageService.SavePhotoAsync(botClient, photoId, session.CarPlate!, "General");
                        session.State = UserState.AwaitingOdometerValue;
                        await botClient.SendMessage(chatId, "Фото сохранено. Введите текущее число на одометре (только цифры):", cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await botClient.SendMessage(chatId, "Пожалуйста, отправьте фотографию.", cancellationToken: cancellationToken);
                    }
                    break;

                case UserState.AwaitingOdometerValue:
                    if (int.TryParse(messageText, out int odometer))
                    {
                        session.OdometerValue = odometer;
                        session.State = UserState.AwaitingOdometerPhoto;
                        await botClient.SendMessage(chatId, "Пробег принят. Отправьте фото одометра.", cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await botClient.SendMessage(chatId, "Пожалуйста, введите корректное целое число.", cancellationToken: cancellationToken);
                    }
                    break;

                case UserState.AwaitingOdometerPhoto:
                    if (message.Photo != null)
                    {
                        var photoId = message.Photo.Last().FileId;
                        session.OdometerPhotoPath = await StorageService.SavePhotoAsync(botClient, photoId, session.CarPlate!, "Odometer");
                        session.State = UserState.AwaitingAnglePhotos;
                        await botClient.SendMessage(chatId, "Фото одометра сохранено. \nТеперь отправляйте фото машины с разных ракурсов. \nКогда закончите, отправьте команду /done", cancellationToken: cancellationToken);
                    }
                    else await botClient.SendMessage(chatId, "Отправьте фото.", cancellationToken: cancellationToken);
                    break;

                case UserState.AwaitingAnglePhotos:
                    if (messageText == "/done")
                    {
                        if (session.AnglePhotoPaths.Count == 0)
                        {
                            await botClient.SendMessage(chatId, "Пришлите хотя бы одно фото или /done для завершения.", cancellationToken: cancellationToken);
                            return;
                        }
                        
                        StorageService.SaveReportToDb(chatId, username, session);
                        
                        await botClient.SendMessage(chatId, "Данные успешно сохранены в базу!", cancellationToken: cancellationToken);
                        session.State = UserState.Idle;
                        session.AnglePhotoPaths.Clear();
                    }
                    else if (message.Photo != null)
                    {
                        var photoId = message.Photo.Last().FileId;
                        var path = await StorageService.SavePhotoAsync(botClient, photoId, session.CarPlate!, "Angles");
                        session.AnglePhotoPaths.Add(path);
                    }
                    break;

                case UserState.ManagerAwaitingPlate:
                    if (string.IsNullOrWhiteSpace(messageText)) return;
                    var requestedPlate = messageText.ToUpper();

                    var data = StorageService.GetReportsByPlate(requestedPlate);
                    if (data.Count == 0)
                    {
                        await botClient.SendMessage(chatId, $"Данных по машине {requestedPlate} не найдено.", cancellationToken: cancellationToken);
                        session.State = UserState.Idle;
                    }
                    else
                    {
                        await botClient.SendMessage(chatId, "Формирую отчет...", cancellationToken: cancellationToken);
                        var excelFile = GenerateExcel(data, requestedPlate);
                        
                        using (var stream = new MemoryStream(excelFile))
                        {
                            // Новый формат отправки файлов InputFile.FromStream
                            await botClient.SendDocument(chatId, 
                                document: InputFile.FromStream(stream, $"{requestedPlate}_Report.xlsx"),
                                caption: "Вот ваш отчет.",
                                cancellationToken: cancellationToken);
                        }
                        session.State = UserState.Idle;
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            await botClient.SendMessage(chatId, "Произошла ошибка при обработке. Начните заново с /start", cancellationToken: cancellationToken);
            session.State = UserState.Idle;
        }
    }

    static byte[] GenerateExcel(List<dynamic> data, string plate)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Отчет");

        worksheet.Cell(1, 1).Value = "Дата загрузки";
        worksheet.Cell(1, 2).Value = "Пользователь";
        worksheet.Cell(1, 3).Value = "Пробег";
        worksheet.Cell(1, 4).Value = "Тип фото";
        worksheet.Cell(1, 5).Value = "Фотография";

        worksheet.Column(1).Width = 20;
        worksheet.Column(2).Width = 15;
        worksheet.Column(3).Width = 10;
        worksheet.Column(4).Width = 15;
        worksheet.Column(5).Width = 40;

        int row = 2;
        foreach (var item in data)
        {
            worksheet.Cell(row, 1).Value = item.CreatedAt;
            worksheet.Cell(row, 2).Value = item.Username;
            worksheet.Cell(row, 3).Value = item.OdometerValue;
            worksheet.Cell(row, 4).Value = item.Type;

            string filePath = item.FilePath;

            if (File.Exists(filePath))
            {
                try 
                {
                    var image = worksheet.AddPicture(filePath)
                        .MoveTo(worksheet.Cell(row, 5))
                        .WithSize(280, 200); 
                    
                    worksheet.Row(row).Height = 155; 
                }
                catch (Exception ex)
                {
                    worksheet.Cell(row, 5).Value = $"Ошибка фото: {ex.Message}";
                }
            }
            else
            {
                worksheet.Cell(row, 5).Value = "Файл не найден";
            }

            row++;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        Console.WriteLine(exception.ToString());
        return Task.CompletedTask;
    }
}