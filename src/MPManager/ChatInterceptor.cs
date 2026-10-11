using System;
using System.Collections.Generic;
using System.Reflection;
using NetworkMessages.FromClient;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Network.Messages;

namespace MPManager;

// Keeps chat commands (messages starting with "!") out of chat. "/" is checked too, but game clients never send
// those: their chat box handles "/" messages itself (MPChatVM.ExecuteSendMessage).
//
// ChatBox broadcasts every message from its handlers for the client chat messages (PlayerMessageAll,
// PlayerMessageTeam). Its OnMessageReceivedAtDedicatedServer hook can observe a message but not stop it. So we
// take those handlers' place in GameNetwork's handler lists:
// - commands are queued for Commands (which also runs mods' commands registered in ModCommands), and passed to
//   OnMessageReceivedAtDedicatedServer for mods that hook chat themselves, but nothing is broadcast;
// - everything else goes to ChatBox's own handler unchanged.
// Tick checks every frame, because ChatBox registers its handlers again whenever the network starts.
internal static class ChatInterceptor
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

    private static readonly FieldInfo HandlersField = typeof(GameNetwork).GetField("_fromClientMessageHandlers", Private);
    private static readonly FieldInfo MessageTypesField = typeof(GameNetwork).GetField("_gameNetworkMessageTypesFromClient", Private);

    public static bool IsCommand(string text)
    {
        return text != null && text.Length > 1 && (text[0] == '!' || text[0] == '/');
    }

    public static void Tick()
    {
        if (!GameNetwork.IsServer)
        {
            return;
        }
        ChatBox chatBox = Game.Current?.GetGameHandler<ChatBox>();
        if (chatBox == null)
        {
            return;
        }
        Hook<PlayerMessageAll>(chatBox, "HandleClientEventPlayerMessageAll", m => m.Message);
        Hook<PlayerMessageTeam>(chatBox, "HandleClientEventPlayerMessageTeam", m => m.Message);
    }

    // Replaces ChatBox's handler for T with an Interceptor, and drops Interceptors left over from an older ChatBox.
    private static void Hook<T>(ChatBox chatBox, string handlerName, Func<T, string> getText) where T : GameNetworkMessage
    {
        if (!(HandlersField?.GetValue(null) is Dictionary<int, List<object>> handlers)
            || !(MessageTypesField?.GetValue(null) is Dictionary<Type, int> messageTypes)
            || !messageTypes.TryGetValue(typeof(T), out int key)
            || !handlers.TryGetValue(key, out List<object> list))
        {
            return;
        }
        list.RemoveAll(item => item is Delegate d && d.Target is IInterceptor interceptor && interceptor.ChatBox != chatBox);

        MethodInfo method = typeof(ChatBox).GetMethod(handlerName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null)
        {
            return;
        }
        // Delegates compare equal by target and method, so this finds ChatBox's registered handler.
        var original = (GameNetworkMessage.ClientMessageHandlerDelegate<T>)Delegate.CreateDelegate(
            typeof(GameNetworkMessage.ClientMessageHandlerDelegate<T>), chatBox, method);
        int index = list.IndexOf(original);
        if (index >= 0)
        {
            list[index] = new Interceptor<T>(chatBox, original, getText).Handler;
            MPManagerSubModule.Log("Chat commands hooked (" + typeof(T).Name + ").");
        }
    }

    private interface IInterceptor
    {
        ChatBox ChatBox { get; }
    }

    private sealed class Interceptor<T> : IInterceptor where T : GameNetworkMessage
    {
        private readonly GameNetworkMessage.ClientMessageHandlerDelegate<T> _original;
        private readonly Func<T, string> _getText;

        public Interceptor(ChatBox chatBox, GameNetworkMessage.ClientMessageHandlerDelegate<T> original, Func<T, string> getText)
        {
            ChatBox = chatBox;
            _original = original;
            _getText = getText;
            Handler = Handle;
        }

        public ChatBox ChatBox { get; }

        public GameNetworkMessage.ClientMessageHandlerDelegate<T> Handler { get; }

        // Runs inside the server's packet handling, where sending network messages crashes the server:
        // commands are only queued here and run in Commands.Tick.
        private bool Handle(NetworkCommunicator peer, T message)
        {
            string text = _getText(message);
            if (!IsCommand(text))
            {
                return _original(peer, message);
            }
            ChatBox.OnMessageReceivedAtDedicatedServer?.Invoke(peer, text);
            Commands.Enqueue(peer, text);
            return true;
        }
    }
}
