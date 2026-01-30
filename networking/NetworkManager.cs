using System;
using System.Collections.Generic;
using Godot;
using Riptide;
using Riptide.Transports.Steam;
using Riptide.Utils;
using Steamworks;

namespace Networking
{
  public partial class NetworkManager : Node
  {
    private enum MessageType : ushort
    {
      OneWayRpc = 0,
      BounceRpc = 1,
      BounceFastRpc = 2,
      SpawnNetworkObject = 10,
      DestroyNetworkObject = 11,
      SyncClient = 12,
    }

    public static Server LocalServer;
    public static Client LocalClient;
    public static bool IsHost => LocalServer != null;
    public static Action<ServerConnectedEventArgs> ClientConnected;
    public static Action<ServerDisconnectedEventArgs> ClientDisconnected;
    public static Action JoinedServer;
    public static Action LeftServer;
    public static CSteamID CurrentLobby;

    private static NetworkManager s_Me;
    private static SteamServer s_LocalSteamServer;

    private Dictionary<uint, NetworkNode> _networkNodeRegistry = new Dictionary<uint, NetworkNode>();
    private uint _nextNetworkObjectId = 0;

    private Callback<LobbyCreated_t> _lobbyCreatedCallback;
    private Callback<LobbyEnter_t> _lobbyEnteredCallback;
    private Callback<GameLobbyJoinRequested_t> _gameLobbyJoinRequestedCallback;

    private float _delay = 0.1f;

    public override void _Ready()
    {
      s_Me = this;

      RiptideLogger.Initialize(GD.Print, GD.Print, GD.PushWarning, GD.PushError, false);

      _lobbyCreatedCallback = Callback<LobbyCreated_t>.Create(LobbyCreated);
      _lobbyEnteredCallback = Callback<LobbyEnter_t>.Create(LobbyEntered);
      _gameLobbyJoinRequestedCallback = Callback<GameLobbyJoinRequested_t>.Create(GameLobbyJoinRequested);
    }

    public override void _PhysicsProcess(double delta)
    {
      if (LocalServer != null) LocalServer.Update();
      if (LocalClient != null) LocalClient.Update();
    }

    public static void Register(Node node, string name, Action<Message> messageHandler)
    {
      NetworkNode source = GetNetworkNode(node);

      if (source == null) throw new Exception("Can not register Rpc for node that does not have a network node!");

      source.Register(node, name, messageHandler);
    }

    public static void Register<ValueType>(Node node, string name, NetworkedVariable<ValueType> syncedVariable)
    {
      NetworkNode source = GetNetworkNode(node);

      if (source == null) throw new Exception("Can not register Rpc for node that does not have a network node!");

      source.Register(node, name, syncedVariable);
    }

    private static void SendToAllRemoteClients(Message message)
    {
      foreach (Connection client in LocalServer.Clients)
      {
        if (client.Id == LocalClient.Id) continue;

        LocalServer.Send(message, client.Id);
      }
    }

    public static NodeType Spawn<NodeType>(string assetId, Node parent, uint authority = 0) where NodeType : Node
    {
      if (!IsHost) throw new Exception("Scenes can not be network spawned from clients!");

      NetworkNode parentNetworkNode = GetNetworkNode(parent);

      string parentPath = parent.GetPath();

      if (parentNetworkNode != null)
      {
        parentPath = parentNetworkNode.GetPathTo(parent);
      }

      GD.Print($"Spawning network object {s_Me._nextNetworkObjectId}! {assetId} {parentNetworkNode} {parentPath}");

      Message message = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.SpawnNetworkObject);
      message.AddString(assetId);
      message.AddBool(parentNetworkNode != null);
      if (parentNetworkNode != null) message.AddUInt(parentNetworkNode.Id);
      message.AddString(parentPath);
      message.AddUInt(authority);
      message.AddUInt(s_Me._nextNetworkObjectId);

      NodeType node = AssetManager.GetScene(assetId).Instantiate<NodeType>();

      NetworkNode networkNode = new NetworkNode();
      networkNode.Id = s_Me._nextNetworkObjectId;
      networkNode.Authority = authority;
      networkNode.AssetId = assetId;
      networkNode.Name = "NetworkNode";

      s_Me._networkNodeRegistry.Add(networkNode.Id, networkNode);

      s_Me._nextNetworkObjectId++;

      node.AddChild(networkNode);

      parent.AddChild(node);

      SendToAllRemoteClients(message);

      return node;
    }

    private static void SpawnRemote(Message message)
    {
      if (IsHost) throw new Exception("Tried spawning remote network object on host?");

      string assetId = message.GetString();

      bool networkNodeRelativeParent = message.GetBool();

      NetworkNode parentNetworkNode = null;
      if (networkNodeRelativeParent) parentNetworkNode = GetNetworkNode(message.GetUInt());

      string parentPath = message.GetString();

      uint authority = message.GetUInt();
      uint id = message.GetUInt();

      GD.Print($"Spawning network object {id} remotely! {assetId} {parentNetworkNode} {parentPath}");

      Node node = AssetManager.GetScene(assetId).Instantiate();

      NetworkNode networkNode = new NetworkNode();
      networkNode.Id = id;
      networkNode.Authority = authority;
      networkNode.AssetId = assetId;
      networkNode.Name = "NetworkNode";

      s_Me._networkNodeRegistry.Add(networkNode.Id, networkNode);

      node.AddChild(networkNode);

      Node parent = parentNetworkNode != null ? parentNetworkNode.GetNode(parentPath) : s_Me.GetNode(parentPath);

      parent.AddChild(node);
    }

    public static void Destroy(Node node)
    {
      if (!IsHost) throw new Exception("Scenes can not be network destroyed from clients!");

      NetworkNode source = GetNetworkNode(node);

      GD.Print($"Destroying network object {source.Id}!");

      Message message = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.DestroyNetworkObject);
      message.AddUInt(source.Id);

      s_Me._networkNodeRegistry.Remove(source.Id);

      SendToAllRemoteClients(message);

      node.GetParent().RemoveChild(node);

      node.QueueFree();
    }

    private static void DestroyRemote(Message message)
    {
      uint id = message.GetUInt();

      NetworkNode source = s_Me._networkNodeRegistry[id];

      s_Me._networkNodeRegistry.Remove(id);

      Node node = source.GetParent();

      node.GetParent().RemoveChild(node);

      node.QueueFree();
    }

    public static NetworkNode GetNetworkNode(Node node)
    {
      if (node.HasNode("NetworkNode"))
      {
        return node.GetNode<NetworkNode>("NetworkNode");
      }

      Node parent = node.GetParent();

      if (parent == null) return null;

      return GetNetworkNode(parent);
    }

    public static bool HasAuthority(Node node)
    {
      NetworkNode networkNode = GetNetworkNode(node);

      if (networkNode == null) throw new Exception("Can not get authority on a node that has no network node and is not a child of a node with a network node!");

      return networkNode.HasAuthority();
    }

    public static NetworkNode GetNetworkNode(uint id)
    {
      if (!s_Me._networkNodeRegistry.ContainsKey(id)) return null;

      return s_Me._networkNodeRegistry[id];
    }

    private static int GetInitialBits(Message message)
    {
      return message.WrittenBits;
    }

    private static Message CloneMessage(Message message, int initialBits)
    {
      Message clonedMessage = Message.Create();
      int bitsToRead = message.WrittenBits - initialBits;
      int readPosition = initialBits;

      while (bitsToRead > 0)
      {
        int bitsToWrite = Math.Min(bitsToRead, 8);

        byte bits;

        message.PeekBits(bitsToWrite, readPosition, out bits);

        clonedMessage.AddBits(bits, bitsToWrite);

        readPosition += 8;
        bitsToRead -= bitsToWrite;
      }

      return clonedMessage;
    }

    public static void SendRpcToServer(Node node, string name, Action<Message> messageBuilder = null, MessageSendMode messageSendMode = MessageSendMode.Reliable)
    {
      NetworkNode source = GetNetworkNode(node);

      if (!IsInstanceValid(source)) GD.PushError("Trying to Send RPC From Invalid Instance " + name);

      Message message = Message.Create(messageSendMode, MessageType.OneWayRpc);
      message.AddUInt(source.Id);
      message.AddString(source.GetLocalPath(node));
      message.AddString(name);

      messageBuilder?.Invoke(message);

      LocalClient.Send(message);
    }

    public static void SendRpcToClients(Node node, string name, Action<Message> messageBuilder = null, MessageSendMode messageSendMode = MessageSendMode.Reliable)
    {
      NetworkNode source = GetNetworkNode(node);

      if (!IsInstanceValid(source)) GD.PushError("Trying to Send RPC From Invalid Instance " + name);

      Message message = Message.Create(messageSendMode, MessageType.OneWayRpc);

      int initialBits = GetInitialBits(message);

      message.AddUInt(source.Id);
      message.AddString(source.GetLocalPath(node));
      message.AddString(name);

      messageBuilder?.Invoke(message);

      Message localMessage = CloneMessage(message, initialBits);

      SendToAllRemoteClients(message);

      s_Me.HandleMessage(localMessage);
    }

    public static void SendRpcToClient(Node node, ushort client, string name, Action<Message> messageBuilder = null, MessageSendMode messageSendMode = MessageSendMode.Reliable)
    {
      NetworkNode source = GetNetworkNode(node);

      if (!IsInstanceValid(source)) GD.PushError("Trying to Send RPC From Invalid Instance " + name);

      Message message = Message.Create(messageSendMode, MessageType.OneWayRpc);
      message.AddUInt(source.Id);
      message.AddString(source.GetLocalPath(node));
      message.AddString(name);

      messageBuilder?.Invoke(message);

      if (LocalClient.Id == client)
      {
        s_Me.HandleMessage(message);
      }
      else
      {
        LocalServer.Send(message, client);
      }
    }

    public static void BounceRpcToClients(Node node, string name, Action<Message> messageBuilder = null, MessageSendMode messageSendMode = MessageSendMode.Reliable)
    {
      NetworkNode source = GetNetworkNode(node);

      if (!IsInstanceValid(source)) GD.PushError("Trying to Send RPC From Invalid Instance " + name);

      Message message = Message.Create(messageSendMode, MessageType.BounceRpc);
      message.AddUInt(source.Id);
      message.AddString(source.GetLocalPath(node));
      message.AddString(name);

      messageBuilder?.Invoke(message);

      LocalClient.Send(message);
    }

    public static void BounceRpcToClientsFast(Node node, string name, Action<Message> messageBuilder = null, MessageSendMode messageSendMode = MessageSendMode.Reliable)
    {
      NetworkNode source = GetNetworkNode(node);

      if (!IsInstanceValid(source as Node)) GD.PushError("Trying to Send RPC From Invalid Instance " + name);

      Message message = Message.Create(messageSendMode, MessageType.BounceFastRpc);

      int initialBits = GetInitialBits(message);

      message.AddUInt(source.Id);
      message.AddString(source.GetLocalPath(node));
      message.AddString(name);

      messageBuilder?.Invoke(message);

      Message localMessage = CloneMessage(message, initialBits);

      LocalClient.Send(message);

      s_Me.HandleMessage(localMessage);
    }

    public static bool Host(ushort port, ushort maxConnections)
    {
      LocalServer = new Server(new Riptide.Transports.Tcp.TcpServer());

      try
      {
        LocalServer.Start(port, maxConnections, 0, false);
      }
      catch
      {
        LocalServer = null;
        return false;
      }

      LocalServer.MessageReceived += s_Me.OnMessageRecieved;

      LocalServer.ClientConnected += s_Me.OnClientConnected;
      LocalServer.ClientDisconnected += s_Me.OnClientDisconnected;

      LocalClient = new Client(new Riptide.Transports.Tcp.TcpClient());
      LocalClient.Connect($"127.0.0.1:{port}", 5, 0, null, false);

      LocalClient.MessageReceived += s_Me.OnMessageRecieved;

      JoinedServer?.Invoke();

      return true;
    }

    public static bool Host()
    {
      GD.Print("Creating lobby...");

      SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, 16);

      s_LocalSteamServer = new SteamServer();
      LocalServer = new Server(s_LocalSteamServer);

      try
      {
        LocalServer.Start(0, 32, 0, false);
      }
      catch
      {
        LocalServer = null;

        return false;
      }

      LocalServer.MessageReceived += s_Me.OnMessageRecieved;

      LocalServer.ClientConnected += s_Me.OnClientConnected;
      LocalServer.ClientDisconnected += s_Me.OnClientDisconnected;

      LocalClient = new Client(new Riptide.Transports.Steam.SteamClient(s_LocalSteamServer));
      LocalClient.Connect("localhost", 5, 0, null, false);

      LocalClient.MessageReceived += s_Me.OnMessageRecieved;

      JoinedServer?.Invoke();

      return true;
    }

    public static bool Join(string ip, ushort port)
    {
      LocalClient = new Client(new Riptide.Transports.Tcp.TcpClient());

      LocalClient.Connect($"{ip}:{port}", 5, 0, null, false);

      LocalClient.MessageReceived += s_Me.OnMessageRecieved;

      JoinedServer?.Invoke();

      return true;
    }

    public static bool Join(CSteamID serverId)
    {
      LocalClient = new Client(new Riptide.Transports.Steam.SteamClient());

      LocalClient.Connect(serverId.ToString(), 5, 0, null, false);

      LocalClient.MessageReceived += s_Me.OnMessageRecieved;

      JoinedServer?.Invoke();

      return true;
    }

    public static void Leave()
    {
      LocalClient.Disconnect();

      if (IsHost)
      {
        LocalServer.Stop();
      }

      LocalClient = null;
      LocalServer = null;

      LeftServer?.Invoke();

      foreach (uint networkId in s_Me._networkNodeRegistry.Keys)
      {
        NetworkNode node = s_Me._networkNodeRegistry[networkId];

        if (!node.IsQueuedForDeletion())
        {
          node.GetParent().QueueFree();
        }
      }

      s_Me._networkNodeRegistry.Clear();
    }

    public static void LeaveLobby()
    {
      if (s_LocalSteamServer == null) return;

      SteamMatchmaking.LeaveLobby(CurrentLobby);
    }

    private void HandleMessage(Message message)
    {
      uint id = message.GetUInt();
      string path = message.GetString();
      string name = message.GetString();

      NetworkNode source = GetNetworkNode(id);

      if (source == null)
      {
        if (message.SendMode == MessageSendMode.Reliable)
        {
          throw new Exception("Can't handle reliable rpc " + name + " for node " + id + ":" + path + " because the network node does not exist!");
        }
        else
        {
          GD.PushWarning("Can't handle unreliable rpc " + name + " for node " + id + ":" + path + " because the network node does not exist!");
        }

        return;
      }

      if (!source.GetParent().HasNode(path))
      {
        if (message.SendMode == MessageSendMode.Reliable)
        {
          throw new Exception("Can't handle reliable rpc " + name + " for node " + id + ":" + path + " because the node does not exist!");
        }
        else
        {
          GD.PushWarning("Can't handle unreliable rpc " + name + " for node " + id + ":" + path + " because the node does not exist!");
        }

        return;
      }

      source.HandleMessage(path, name, message);
    }

    private void OnMessageRecieved(object _, MessageReceivedEventArgs eventArguments)
    {
      if (eventArguments.MessageId == 1 || eventArguments.MessageId == 2)
      {
        Message relayMessage = Message.Create(eventArguments.Message.SendMode, 0);

        while (eventArguments.Message.UnreadBits > 0)
        {
          int bitsToWrite = Math.Min(eventArguments.Message.UnreadBits, 8);

          byte bits;

          eventArguments.Message.GetBits(bitsToWrite, out bits);

          relayMessage.AddBits(bits, bitsToWrite);
        }

        if (eventArguments.MessageId == 1)
        {
          LocalServer.SendToAll(relayMessage);
        }
        else
        {
          foreach (Connection connection in LocalServer.Clients)
          {
            if (connection == eventArguments.FromConnection) continue;

            LocalServer.Send(relayMessage, connection.Id);
          }
        }

        return;
      }

      if (eventArguments.MessageId == (ushort)MessageType.SpawnNetworkObject)
      {
        SpawnRemote(eventArguments.Message);

        return;
      }

      if (eventArguments.MessageId == (ushort)MessageType.DestroyNetworkObject)
      {
        DestroyRemote(eventArguments.Message);

        return;
      }

      if (eventArguments.MessageId == (ushort)MessageType.SyncClient)
      {
        SyncClient(eventArguments.Message);

        return;
      }

      HandleMessage(eventArguments.Message);
    }

    private void OnClientConnected(object server, ServerConnectedEventArgs eventArguments)
    {
      if (eventArguments.Client.Id != LocalClient.Id)
      {
        GD.Print($"Remote client {eventArguments.Client.Id} connected!");

        Message message = Message.Create(MessageSendMode.Reliable, MessageType.SyncClient);

        message.AddInt(_networkNodeRegistry.Keys.Count);

        foreach (KeyValuePair<uint, NetworkNode> pair in _networkNodeRegistry)
        {
          NetworkNode source = pair.Value;

          Node parent = source.GetParent().GetParent();
          NetworkNode parentNetworkNode = GetNetworkNode(parent);

          string parentPath = parent.GetPath();

          if (parentNetworkNode != null)
          {
            parentPath = parentNetworkNode.GetPathTo(parent);
          }

          message.AddString(source.AssetId);
          message.AddBool(parentNetworkNode != null);
          if (parentNetworkNode != null) message.AddUInt(parentNetworkNode.Id);
          message.AddString(parentPath);
          message.AddUInt(source.Authority);
          message.AddUInt(pair.Key);
        }

        LocalServer.Send(message, eventArguments.Client);
      }

      ClientConnected?.Invoke(eventArguments);
    }

    private void OnClientDisconnected(object server, ServerDisconnectedEventArgs eventArguments)
    {
      if (eventArguments.Client.Id != LocalClient.Id) GD.Print($"Remote client {eventArguments.Client.Id} disconnected!");

      ClientDisconnected?.Invoke(eventArguments);
    }

    private void SyncClient(Message message)
    {
      int nodesToSpawn = message.GetInt();

      for (int index = 0; index < nodesToSpawn; index++)
      {
        string assetId = message.GetString();

        bool networkNodeRelativeParent = message.GetBool();

        NetworkNode parentNetworkNode = null;
        if (networkNodeRelativeParent) parentNetworkNode = GetNetworkNode(message.GetUInt());

        string parentPath = message.GetString();

        uint authority = message.GetUInt();
        uint id = message.GetUInt();

        GD.Print($"Syncing network object {id} remotely! {assetId} {parentPath}");

        Node node = AssetManager.GetScene(assetId).Instantiate();

        NetworkNode networkNode = new NetworkNode();
        networkNode.Id = id;
        networkNode.Authority = authority;
        networkNode.AssetId = assetId;
        networkNode.Name = "NetworkNode";

        s_Me._networkNodeRegistry.Add(networkNode.Id, networkNode);

        node.AddChild(networkNode);

        Node parent = parentNetworkNode != null ? parentNetworkNode.GetNode(parentPath) : s_Me.GetNode(parentPath);

        parent.AddChild(node);
      }
    }

    private void LobbyCreated(LobbyCreated_t lobbyCreated)
    {
      GD.Print("Created lobby! " + (lobbyCreated.m_eResult == EResult.k_EResultOK));

      SteamMatchmaking.SetLobbyData((CSteamID)lobbyCreated.m_ulSteamIDLobby, "name", "Project Squad Test Lobby");
      SteamMatchmaking.SetLobbyGameServer((CSteamID)lobbyCreated.m_ulSteamIDLobby, default, default, SteamUser.GetSteamID());

      CurrentLobby = (CSteamID)lobbyCreated.m_ulSteamIDLobby;
    }

    private void LobbyEntered(LobbyEnter_t lobbyEntered)
    {
      if (IsHost) return;

      GD.Print("Entered lobby! " + SteamMatchmaking.GetLobbyData((CSteamID)lobbyEntered.m_ulSteamIDLobby, "name"));

      SteamMatchmaking.GetLobbyGameServer((CSteamID)lobbyEntered.m_ulSteamIDLobby, out uint ip, out ushort port, out CSteamID serverId);

      CurrentLobby = (CSteamID)lobbyEntered.m_ulSteamIDLobby;

      Join(serverId);
    }

    private void GameLobbyJoinRequested(GameLobbyJoinRequested_t gameLobbyJoinRequested)
    {
      GD.Print("Requested join lobby! " + SteamMatchmaking.GetLobbyData(gameLobbyJoinRequested.m_steamIDLobby, "name"));

      SteamMatchmaking.JoinLobby(gameLobbyJoinRequested.m_steamIDLobby);
    }
  }
}