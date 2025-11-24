using System.Text.Json;
using System.Text.Json.Serialization;
using System;

namespace Sphene.UI.Theme;

public class Vector4JsonConverter : JsonConverter<System.Numerics.Vector4>
{
    public override System.Numerics.Vector4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();

        float x = 0, y = 0, z = 0, w = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var propertyName = reader.GetString();
                reader.Read();

                switch (propertyName?.ToLowerInvariant())
                {
                    case "x":
                        x = reader.GetSingle();
                        break;
                    case "y":
                        y = reader.GetSingle();
                        break;
                    case "z":
                        z = reader.GetSingle();
                        break;
                    case "w":
                        w = reader.GetSingle();
                        break;
                }
            }
        }

        return new System.Numerics.Vector4(x, y, z, w);
    }

    public override void Write(Utf8JsonWriter writer, System.Numerics.Vector4 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", RoundSmallValue(value.X));
        writer.WriteNumber("y", RoundSmallValue(value.Y));
        writer.WriteNumber("z", RoundSmallValue(value.Z));
        writer.WriteNumber("w", RoundSmallValue(value.W));
        writer.WriteEndObject();
    }

    private static float RoundSmallValue(float value)
    {
        if (Math.Abs(value) < 1e-10f)
            return 0f;
        if (Math.Abs(value) < 1e-5f)
            return (float)Math.Round(value, 6);
        return value;
    }
}
