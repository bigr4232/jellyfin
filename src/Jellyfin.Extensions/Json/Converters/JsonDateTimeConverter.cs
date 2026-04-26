using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Extensions.Json.Converters
{
    /// <summary>
    /// DateTime converter that emits ISO 8601 with exactly 3 fractional-second digits.
    /// </summary>
    public class JsonDateTimeConverter : JsonConverter<DateTime>
    {
        /// <inheritdoc />
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.GetDateTime();
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            // Swift's JSONDecoder iso8601 strategy with withFractionalSeconds requires exactly 3
            // fractional digits; the .NET round-trip ("O") format emits up to 7, which Swift rejects
            // and breaks the native iOS app's SyncPlay clock-offset decoding.
            writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture));
        }
    }
}
