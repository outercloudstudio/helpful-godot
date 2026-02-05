using System;
using Godot;
using Riptide;

namespace Networking;

public class NetworkedVariable<T>
{
    public bool Synced = false;
    public T Value
    {
        get => _value;
        set
        {
            Synced = true;

            if (_syncMode == VariableSyncMode.Auto && !_value.Equals(value))
            {
                SendUpdate();
            }

            _value = value;
        }
    }
    
    private T _value;
    
    private Node _node;
    private NetworkNode _source;
    private string _varName;

    private ulong _lastSentTick;
    private int _lastSentIndex = -1;
    private int _lastRecievedIndex = -1;
    
    private readonly uint _minSendDelay;
    private readonly VariableAuthority _authority;
    private readonly VariableSyncMode _syncMode;
    private readonly MessageSendMode _msgSendMode;

    public NetworkedVariable(T defaultValue, uint minSendDelay = 50, VariableAuthority authority = VariableAuthority.NodeOwner, VariableSyncMode syncMode = VariableSyncMode.Manual, MessageSendMode msgSendMode = MessageSendMode.Unreliable)
    {
        _value = defaultValue;
        _minSendDelay = minSendDelay;
        _authority = authority;
        _syncMode = syncMode;
        _msgSendMode = msgSendMode;
    }

    public void Register(Node node, NetworkNode source, string varName)
    {
        _node = node;
        _source = source;
        _varName = varName;
    }

    public void Sync()
    {
        SendUpdate();
    }

    private void SendUpdate()
    {
        if (_source == null)
        {
            throw new Exception("Can not send updates for an unregistered network variable!");
        }

        ulong now = Time.GetTicksMsec();
        
        if (now - _lastSentTick < _minSendDelay) return;

        _lastSentTick = now;
    
        if (_authority == VariableAuthority.Server && NetworkManager.IsHost)
        {
            NetworkManager.SendRpcToClients(_node, _varName, BuildMessage(false), _msgSendMode);
            return;
        }
        if (_authority == VariableAuthority.NodeOwner && _source.HasAuthority())
        {
            NetworkManager.SendRpcToServer(_node, _varName, BuildMessage(true), _msgSendMode);
        }
    }

    public void ReceiveUpdate(Message msg)
    {
        bool bounceToClients = msg.GetBool();
        int index = msg.GetInt();

        if (index <= _lastRecievedIndex) return;

        _lastRecievedIndex = index;
        _lastSentIndex = Math.Max(_lastSentIndex, _lastRecievedIndex);

        Synced = true;

        if (!_source.HasAuthority())
        {
            _value = (T) NetworkedVariableTypes.Decode(typeof(T), msg);
        }

        if (bounceToClients)
        {
            NetworkManager.SendRpcToClients(_node, _varName, BuildMessage(false), _msgSendMode);
        }
    }

    private Action<Message> BuildMessage(bool bounceToClients)
    {
        return (Message msg) =>
        {
            msg.AddBool(bounceToClients);

            _lastSentIndex++;

            msg.AddInt(_lastSentIndex);

            NetworkedVariableTypes.Encode(typeof(T), msg, _value);
        };
    }
}

public enum VariableSyncMode
{
    Manual,
    Auto
}

public enum VariableAuthority
{
    NodeOwner,
    Server
}

