namespace FieldTrader.Market;

// Only throw before any button-down/up or wheel submission has been attempted.
public sealed class InputNotSentException(string message) : Exception(message);
