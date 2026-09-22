namespace Enightx.Pos.Common;

public class PosException : Exception
{
    public PosException(string message) : base(message) { }
    public PosException(string message, Exception inner) : base(message, inner) { }
}

public class AuthenticationException : PosException
{
    public AuthenticationException(string message) : base(message) { }
}

public class InsufficientTenderException : PosException
{
    public InsufficientTenderException(string message) : base(message) { }
}

public class InsufficientStockException : PosException
{
    public InsufficientStockException(string message) : base(message) { }
}

public class UnauthorizedActionException : PosException
{
    public UnauthorizedActionException(string message) : base(message) { }
}

public class ShiftClosedException : PosException
{
    public ShiftClosedException(string message) : base(message) { }
}

public class PrinterException : PosException
{
    public PrinterException(string message) : base(message) { }
    public PrinterException(string message, Exception inner) : base(message, inner) { }
}

public class CreditLimitExceededException : PosException
{
    public CreditLimitExceededException(string message) : base(message) { }
}

public class LicenseExpiredException : PosException
{
    public LicenseExpiredException(string message = "License has expired and offline grace period has ended. Checkout is locked.") : base(message) { }
    public LicenseExpiredException(string message, Exception inner) : base(message, inner) { }
}

public class LicenseLockoutException : LicenseExpiredException
{
    public LicenseLockoutException(string message = "License has expired and offline grace period has ended. Checkout is locked.") : base(message) { }
    public LicenseLockoutException(string message, Exception inner) : base(message, inner) { }
}

