namespace TGBotForCars;

// Состояния пользователя для "диалога"
public enum UserState
{
    Idle,
    AwaitingPlate,
    AwaitingGeneralPhoto,
    AwaitingOdometerValue,
    AwaitingOdometerPhoto,
    AwaitingAnglePhotos,
    ManagerAwaitingPlate
}

// Данные текущей сессии пользователя (в памяти)
public class UserSession
{
    public UserState State { get; set; } = UserState.Idle;
    public string? CarPlate { get; set; }
    public string? GeneralPhotoPath { get; set; }
    public int OdometerValue { get; set; }
    public string? OdometerPhotoPath { get; set; }
    public List<string> AnglePhotoPaths { get; set; } = new();
}

// Модель для сохранения в БД
public class CarReport
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public string Username { get; set; }
    public string CarPlate { get; set; }
    public int OdometerValue { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ReportPhoto
{
    public int Id { get; set; }
    public int ReportId { get; set; }
    public string FilePath { get; set; }
    public string Type { get; set; } // General, Odometer, Angle
}