using System.Net;
using System.Net.Http;
using System.Text.Json;
using LoRPoCTracker.Models;

namespace LoRPoCTracker.Services;

/// <summary>
/// 访问 LoR 客户端本地接口（默认 http://127.0.0.1:21337）。
/// </summary>
public sealed class LorApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly int _port;

    public LorApiClient(int port)
    {
        _port = port;
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
    }

    private string BaseUrl => $"http://127.0.0.1:{_port}";

    /// <summary>获取当前屏幕卡牌位置；接口不可用时返回 null。</summary>
    public async Task<GameSnapshot?> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"{BaseUrl}/positional-rectangles", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<SnapshotDto>(stream, JsonOptions, ct).ConfigureAwait(false);
            if (dto is null) return null;

            return new GameSnapshot
            {
                PlayerName = string.IsNullOrWhiteSpace(dto.PlayerName) ? null : dto.PlayerName,
                OpponentName = string.IsNullOrWhiteSpace(dto.OpponentName) ? null : dto.OpponentName,
                GameState = dto.GameState ?? "Menus",
                ScreenWidth = dto.Screen?.ScreenWidth ?? 0,
                ScreenHeight = dto.Screen?.ScreenHeight ?? 0,
                Rectangles = (dto.Rectangles ?? new List<RectDto>())
                    .Select(r => new CardRectangle
                    {
                        CardId = r.CardID,
                        CardCode = r.CardCode ?? "",
                        TopLeftX = r.TopLeftX,
                        TopLeftY = r.TopLeftY,
                        Width = r.Width,
                        Height = r.Height,
                        LocalPlayer = r.LocalPlayer
                    })
                    .ToList()
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取本局牌库（仅本地玩家）；不在对局中时返回空。</summary>
    public async Task<StaticDeck?> GetStaticDeckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"{BaseUrl}/static-decklist", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<StaticDeckDto>(stream, JsonOptions, ct).ConfigureAwait(false);
            if (dto is null) return null;

            return new StaticDeck
            {
                DeckCode = string.IsNullOrWhiteSpace(dto.DeckCode) ? null : dto.DeckCode,
                CardsInDeck = dto.CardsInDeck ?? new Dictionary<string, int>(StringComparer.Ordinal)
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<GameResult?> GetGameResultAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"{BaseUrl}/game-result", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<GameResult>(stream, JsonOptions, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed class SnapshotDto
    {
        public string? PlayerName { get; set; }
        public string? OpponentName { get; set; }
        public string? GameState { get; set; }
        public ScreenDto? Screen { get; set; }
        public List<RectDto>? Rectangles { get; set; }
    }

    private sealed class ScreenDto
    {
        public int ScreenWidth { get; set; }
        public int ScreenHeight { get; set; }
    }

    private sealed class RectDto
    {
        public int CardID { get; set; }
        public string? CardCode { get; set; }
        public int TopLeftX { get; set; }
        public int TopLeftY { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool LocalPlayer { get; set; }
    }

    private sealed class StaticDeckDto
    {
        public string? DeckCode { get; set; }
        public Dictionary<string, int>? CardsInDeck { get; set; }
    }
}
