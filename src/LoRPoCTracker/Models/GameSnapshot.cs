namespace LoRPoCTracker.Models;

/// <summary>/positional-rectangles 的响应。</summary>
public sealed class GameSnapshot
{
    public string? PlayerName { get; set; }
    public string? OpponentName { get; set; }
    public string GameState { get; set; } = "Menus";
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public List<CardRectangle> Rectangles { get; set; } = new();

    public bool IsInGame => string.Equals(GameState, "InProgress", StringComparison.OrdinalIgnoreCase);
}

/// <summary>屏幕上的一张卡（手牌 / 场上 / 堆叠）。</summary>
public sealed class CardRectangle
{
    public int CardId { get; set; }
    public string CardCode { get; set; } = "";
    public int TopLeftX { get; set; }
    public int TopLeftY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool LocalPlayer { get; set; }
}

/// <summary>/static-decklist 的响应，仅对本地玩家提供。</summary>
public sealed class StaticDeck
{
    public string? DeckCode { get; set; }
    public Dictionary<string, int> CardsInDeck { get; set; } = new();

    public bool HasDeck => CardsInDeck.Count > 0;
}

/// <summary>/game-result 的响应。</summary>
public sealed class GameResult
{
    public int GameID { get; set; }
    public bool LocalPlayerWon { get; set; }
}
