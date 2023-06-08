using System.Runtime.Serialization;

namespace Unreified;

public class LeftOverStepException : Exception
{
    public LeftOverStepException() : base()
    {
    }

    public LeftOverStepException(string message) : base(message)
    {
    }

    public LeftOverStepException(string message, Exception innerException) : base(message, innerException)
    {
    }

    protected LeftOverStepException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}
