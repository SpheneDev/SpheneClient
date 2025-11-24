using System.Text.Json;
using System.Text.Json.Serialization;
using System;

namespace Sphene.UI.Theme;

public class Vector2JsonConverter : JsonConverter<System.Numerics.Vector2>
{
    public override System.Numerics.Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();

        float x = 0, y = 0;

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
                }
            }
        }

        return new System.Numerics.Vector2(x, y);
    }

    public override void Write(Utf8JsonWriter writer, System.Numerics.Vector2 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", RoundSmallValue(value.X));
        writer.WriteNumber("y", RoundSmallValue(value.Y));
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
