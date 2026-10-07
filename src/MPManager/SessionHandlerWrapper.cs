using System.Threading.Tasks;
using TaleWorlds.MountAndBlade.Diamond;
using TaleWorlds.PlayerServices;

namespace MPManager;

// Stands in for the game's lobby session handler (DedicatedCustomGameServerHandler) and passes every callback
// through, except a lost connection once a game is registered: LobbyReconnect handles that instead of the
// handler's 20 s shutdown.
internal sealed class SessionHandlerWrapper : ICustomBattleServerSessionHandler
{
    private readonly ICustomBattleServerSessionHandler _inner;

    public SessionHandlerWrapper(ICustomBattleServerSessionHandler inner)
    {
        _inner = inner;
    }

    public void OnCantConnect()
    {
        if (!LobbyReconnect.OnConnectionLost())
        {
            _inner.OnCantConnect();
        }
    }

    public void OnDisconnected()
    {
        if (!LobbyReconnect.OnConnectionLost())
        {
            _inner.OnDisconnected();
        }
    }

    public void OnConnected() => _inner.OnConnected();

    public void OnStateChanged(CustomBattleServer.State state) => _inner.OnStateChanged(state);

    public void OnSuccessfulGameRegister() => _inner.OnSuccessfulGameRegister();

    public Task<PlayerJoinGameResponseDataFromHost[]> OnClientWantsToConnectCustomGame(PlayerJoinGameData[] playerJoinData) =>
        _inner.OnClientWantsToConnectCustomGame(playerJoinData);

    public void OnClientQuitFromCustomGame(PlayerId playerId) => _inner.OnClientQuitFromCustomGame(playerId);

    public void OnGameFinished() => _inner.OnGameFinished();

    public void OnChatFilterListsReceived(string[] profanityList, string[] allowList) => _inner.OnChatFilterListsReceived(profanityList, allowList);

    public void OnPlayerKickRequested(PlayerId playerID, bool isBanning) => _inner.OnPlayerKickRequested(playerID, isBanning);
}
