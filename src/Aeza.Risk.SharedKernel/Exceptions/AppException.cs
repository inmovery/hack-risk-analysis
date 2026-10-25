namespace Aeza.Risk.SharedKernel.Exceptions;

/// <summary>Исключение уровня приложения</summary>
public class AppException : Exception
{
    /// <summary>Создаёт исключение без сообщения</summary>
    public AppException()
    {
    }

    /// <summary>Создаёт исключение с сообщением</summary>
    public AppException(string message) : base(message)
    {
    }

    /// <summary>Создаёт исключение с сообщением и причиной</summary>
    public AppException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
