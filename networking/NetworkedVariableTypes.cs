using System;
using System.Collections.Generic;
using Godot;
using Riptide;

namespace Networking;

public class NetworkedVariableTypes
{
    private static Dictionary<Type, Action<Message, object>> encoders = new();
    private static Dictionary<Type, Func<Message, object>> decoders = new();

    public static void RegisterEncoder(Type type, Action<Message, object> encoder)
    {
        encoders[type] = encoder;
    }

    public static void RegisterDecoder(Type type, Func<Message, object> decoder)
    {
        decoders[type] = decoder;
    }

    public static void Encode(Type type, Message msg, object value)
    {
        encoders[type](msg, value);
    }

    public static object Decode(Type type, Message msg)
    {
        return decoders[type](msg);
    }

    public static void RegisterBuiltIn()
    {
        RegisterEncoder(typeof(int), (msg, value) => msg.AddInt((int)value));
        RegisterDecoder(typeof(int), (msg) => msg.GetInt());

        RegisterEncoder(typeof(float), (msg, value) => msg.AddFloat((float)value));
        RegisterDecoder(typeof(float), (msg) => msg.GetFloat());

        RegisterEncoder(typeof(double), (msg, value) => msg.AddDouble((float)value));
        RegisterDecoder(typeof(double), (msg) => msg.GetDouble());

        RegisterEncoder(typeof(bool), (msg, value) => msg.AddBool((bool)value));
        RegisterDecoder(typeof(bool), (msg) => msg.GetBool());

        RegisterEncoder(typeof(Vector2), (msg, value) =>
        {
            Vector2 vec = (Vector2)value;

            msg.AddFloat(vec.X);
            msg.AddFloat(vec.Y);
        });
        RegisterDecoder(typeof(Vector2), (msg) =>
        {
            float x = msg.GetFloat();
            float y = msg.GetFloat();

            return new Vector2(x, y);
        });

        RegisterEncoder(typeof(Vector3), (msg, value) =>
        {
            Vector3 vec = (Vector3)value;

            msg.AddFloat(vec.X);
            msg.AddFloat(vec.Y);
            msg.AddFloat(vec.Z);
        });
        RegisterDecoder(typeof(Vector3), (msg) =>
        {
            float x = msg.GetFloat();
            float y = msg.GetFloat();
            float z = msg.GetFloat();

            return new Vector3(x, y, z);
        });
    }
}