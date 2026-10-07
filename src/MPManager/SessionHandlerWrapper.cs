using System.Threading.Tasks;
using TaleWorlds.MountAndBlade.Diamond;
using TaleWorlds.PlayerServices;

namespace MPManager;

// Stands in for the game's lobby session handler (DedicatedCustomGameServerHandler) and passes every callback
// through, except:
// - a lost connection once a game is registered: LobbyReconnect handles that instead of the handler's 20 s shutdown;
// - OnSuccessfulGameRegister after the first one. The handler sets the game up there: it adds a
//   CustomGameServerNetworkHandler and pushes a LobbyGameStateDedicatedCustomServer. A re-registration by
//   LobbyReconnect reuses the same CustomBattleServer, so that setup is still in place; running it again
//   duplicated it, every client message was handled twice (double votes) and clients ran out of memory.
internal sealed class SessionHandlerWrapper : ICustomBattleServerSessionHandler
{
    private readonly ICustomBattleServerSessionHandler _inner;

    private bool _gameRegistered;

    // gameRegistered: the game already went through OnSuccessfulGameRegister before we were attached.
    public SessionHandlerWrapper(ICustomBattleServerSessionHandler inner, bool gameRegistered)
    {
        _inner = inner;
        _gameRegistered = gameRegistered;
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

    public void OnSuccessfulGameRegister()
    {
        if (_gameRegistered)
        {
            MPManagerSubModule.Log("Game registered again; keeping the existing game setup.");
            return;
        }
        _gameRegistered = true;
        _inner.OnSuccessfulGameRegister();
    }

    public Task<PlayerJoinGameResponseDataFromHost[]> OnClientWantsToConnectCustomGame(PlayerJoinGameData[] playerJoinData) =>
        _inner.OnClientWantsToConnectCustomGame(playerJoinData);

    public void OnClientQuitFromCustomGame(PlayerId playerId) => _inner.OnClientQuitFromCustomGame(playerId);

    public void OnGameFinished() => _inner.OnGameFinished();

    public void OnChatFilterListsReceived(string[] profanityList, string[] allowList) => _inner.OnChatFilterListsReceived(profanityList, allowList);

    public void OnPlayerKickRequested(PlayerId playerID, bool isBanning) => _inner.OnPlayerKickRequested(playerID, isBanning);
}
