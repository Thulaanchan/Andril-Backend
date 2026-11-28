namespace Andril.Economy.Wallet;

public class CoinDomainException : Exception
{
    public CoinDomainException(string message) : base(message) { }
    public CoinDomainException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class InvalidCoinAmountException : CoinDomainException
{
    public InvalidCoinAmountException(string message) : base(message) { }
}

public sealed class InsufficientCoinBalanceException : CoinDomainException
{
    public InsufficientCoinBalanceException(string message) : base(message) { }
}

public sealed class DuplicateCoinTransactionException : CoinDomainException
{
    public DuplicateCoinTransactionException(string message) : base(message) { }
}

public sealed class CoinWalletNotFoundException : CoinDomainException
{
    public CoinWalletNotFoundException(string message) : base(message) { }
}

public sealed class InvalidCoinOperationException : CoinDomainException
{
    public InvalidCoinOperationException(string message) : base(message) { }
}
