using System.Runtime.Serialization;

namespace Unreified;

public class DuplicateResultException : Exception
{
    public DuplicateResultException() : base()
    {
    }

    public DuplicateResultException(string message) : base(message)
    {
    }

    public DuplicateResultException(string message, Exception innerException) : base(message, innerException)
    {
    }

    protected DuplicateResultException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}
