namespace PSR.Service.Api.Stock;

/// <summary>A stock business-rule violation (insufficient stock, bad state). Surfaced as HTTP 400.</summary>
public class StockException(string message) : Exception(message);
