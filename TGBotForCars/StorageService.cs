using Dapper;
using Microsoft.Data.Sqlite;
using Telegram.Bot;

namespace TGBotForCars;

public class StorageService
{
    private const string ConnectionString = "Data Source=cars.db";
    private const string BasePhotoPath = "Storage";

    public static void Initialize()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS Reports (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ChatId INTEGER,
                Username TEXT,
                CarPlate TEXT,
                OdometerValue INTEGER,
                CreatedAt TEXT
            );
            CREATE TABLE IF NOT EXISTS Photos (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ReportId INTEGER,
                FilePath TEXT,
                Type TEXT
            );");

        if (!Directory.Exists(BasePhotoPath))
            Directory.CreateDirectory(BasePhotoPath);
    }

    public static async Task<string> SavePhotoAsync(ITelegramBotClient bot, string fileId, string carPlate, string subFolder)
    {
        // В новой версии метод называется GetFile (без Async, но он awaitable)
        var file = await bot.GetFile(fileId);
        
        var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
        var folderPath = Path.Combine(BasePhotoPath, carPlate, dateFolder, subFolder);
        Directory.CreateDirectory(folderPath);

        var fileName = $"{Guid.NewGuid()}.jpg";
        var fullPath = Path.Combine(folderPath, fileName);

        using var saveImageStream = new FileStream(fullPath, FileMode.Create);
        // В новой версии DownloadFile принимает путь и стрим
        await bot.DownloadFile(file.FilePath!, saveImageStream);

        return fullPath;
    }

    public static void SaveReportToDb(long chatId, string username, UserSession session)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();
        
        try
        {
            var reportId = connection.QuerySingle<int>(@"
                INSERT INTO Reports (ChatId, Username, CarPlate, OdometerValue, CreatedAt)
                VALUES (@ChatId, @Username, @CarPlate, @OdometerValue, @CreatedAt)
                RETURNING Id;", 
                new { 
                    ChatId = chatId, 
                    Username = username, 
                    session.CarPlate, 
                    session.OdometerValue, 
                    CreatedAt = DateTime.Now 
                }, transaction);

            if (session.GeneralPhotoPath != null)
                InsertPhoto(connection, transaction, reportId, session.GeneralPhotoPath, "General");
            
            if (session.OdometerPhotoPath != null)
                InsertPhoto(connection, transaction, reportId, session.OdometerPhotoPath, "Odometer");

            foreach (var path in session.AnglePhotoPaths)
            {
                InsertPhoto(connection, transaction, reportId, path, "Angle");
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void InsertPhoto(SqliteConnection conn, SqliteTransaction tran, int reportId, string path, string type)
    {
        conn.Execute("INSERT INTO Photos (ReportId, FilePath, Type) VALUES (@ReportId, @Path, @Type)",
            new { ReportId = reportId, Path = path, Type = type }, tran);
    }

    public static List<dynamic> GetReportsByPlate(string plate)
    {
        using var connection = new SqliteConnection(ConnectionString);
        return connection.Query<dynamic>(@"
            SELECT r.CreatedAt, r.Username, r.OdometerValue, p.Type, p.FilePath
            FROM Reports r
            JOIN Photos p ON r.Id = p.ReportId
            WHERE r.CarPlate = @Plate
            ORDER BY r.CreatedAt DESC", new { Plate = plate }).ToList();
    }
}