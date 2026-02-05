using Godot;
using Networking;
using Riptide;
using System;
using System.Collections.Generic;

public partial class NetworkNode : Node {
    public uint Id;
    public uint Authority;
    public string AssetId;

    private Dictionary<string, Action<Message>> _registeredMessageHandlers = new Dictionary<string, Action<Message>>();

    public void Register(Node node, string name, Action<Message> messageHandler) {
        _registeredMessageHandlers.Add(GetLocalPath(node) + ":" + name, messageHandler);

        GD.Print($"Registered rpc ${GetLocalPath(node) + ":" + name}");
    }

    public void Register<T>(Node node, string name, NetworkedVariable<T> syncedVariable) {
        syncedVariable.Register(node, this, name);

        _registeredMessageHandlers.Add(GetLocalPath(node) + ":" + name, syncedVariable.ReceiveUpdate);

        GD.Print($"Registered network variable ${GetLocalPath(node) + ":" + name}");
    }

    public bool HasAuthority() {
        return NetworkManager.IsHost && Authority == 0 || Authority == NetworkManager.LocalClient.Id;
    }

    public void HandleMessage(string path, string name, Message message) {
        _registeredMessageHandlers[path + ":" + name].Invoke(message);
    }

    public string GetLocalPath(Node node) {
        return GetParent().GetPathTo(node);
    }
}
