namespace Shared.WeatherStation;

using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using System.IO;

public class FlexibleStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetInt32().ToString(), // handles numbers
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token {reader.TokenType}")
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

public class StationIdentifiers
{
    [JsonPropertyName("national")]
    public string National { get; set; }
    
    [JsonPropertyName("wmo")]
    public string Wmo { get; set; }

    [JsonPropertyName("icao")]
    public string Icao { get; set; }

    public override string ToString()
    {
        return $"National={National}, WMO={Wmo}, ICAO={Icao}";
    }
}

public class StationLocation
{
    [JsonPropertyName("latitude")]
    public Double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public Double Longitude { get; set; }

    [JsonPropertyName("elevation")]
    public int Elevation { get; set; }

    public override string ToString()
    {
        return $"Lat={Latitude}, Lon={Longitude}, Elev={Elevation}m";
    }
}

public class InventoryTimeStamp
{
    [JsonPropertyName("start")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Start { get; set; }

    [JsonPropertyName("end")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string End { get; set; }

    public override string ToString()
    {
        return $"{Start} → {End}";
    }
}

public class StationInventory
{
    [JsonPropertyName("hourly")]
    public InventoryTimeStamp Hourly { get; set; }

    [JsonPropertyName("daily")]
    public InventoryTimeStamp Daily { get; set; }
    
    [JsonPropertyName("monthly")]
    public InventoryTimeStamp Monthly { get; set; }

    [JsonPropertyName("normals")]
    public InventoryTimeStamp Normals { get; set; }

    public override string ToString()
    {
        return $"Hourly: {Hourly}, Daily: {Daily}, Monthly: {Monthly}, Normals: {Normals}";
    }
}

public class WeatherStation
{
    [JsonPropertyName("id")]
    public String Id { get; set; }

    [JsonPropertyName("name")]
    public Dictionary<string, string> Name  { get; set; }

    [JsonPropertyName("country")]
    public String Country { get; set; }

    [JsonPropertyName("region")]
    public String Region { get; set; }

    [JsonPropertyName("identifiers")]
    public StationIdentifiers Identifiers { get; set; }

    [JsonPropertyName("location")]
    public StationLocation Location { get; set; }

    [JsonPropertyName("timezone")]
    public String TimeZone { get; set; }

    [JsonPropertyName("inventory")]
    public StationInventory Inventory { get; set; }

    public override string ToString()
    {
        string names = Name != null
            ? string.Join(", ", Name.Select(kv => $"{kv.Key}:{kv.Value}"))
            : "none";

        return 
            $"Station {Id}\n" +
            $"  Names: {names}\n" +
            $"  Country: {Country}, Region: {Region}\n" +
            $"  Identifiers: {Identifiers}\n" +
            $"  Location: {Location}\n" +
            $"  Timezone: {TimeZone}\n" +
            $"  Inventory: {Inventory}";
    }
}