namespace Chameleon.Core;

/// <summary>
/// Нарушение протокола удалённой стороной. Правильная реакция - закрыть несущую
/// молча, не отправляя ничего, что могло бы помочь активному зонду.
/// </summary>
public sealed class ChameleonProtocolException(string message) : Exception(message);