using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AetherisLauncher.Models
{
    public class CurseForgeManifest
    {
        [JsonPropertyName("minecraft")]
        public CurseForgeMinecraftInfo Minecraft { get; set; } = new();

        [JsonPropertyName("manifestType")]
        public string ManifestType { get; set; } = "minecraftModpack";

        [JsonPropertyName("manifestVersion")]
        public int ManifestVersion { get; set; } = 1;

        [JsonPropertyName("name")]
        public string Name { get; set; } = "Imported Modpack";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        [JsonPropertyName("author")]
        public string Author { get; set; } = "";

        [JsonPropertyName("files")]
        public List<CurseForgeFileItem> Files { get; set; } = new();

        [JsonPropertyName("overrides")]
        public string Overrides { get; set; } = "overrides";
    }

    public class CurseForgeMinecraftInfo
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.20.1";

        [JsonPropertyName("modLoaders")]
        public List<CurseForgeModLoaderInfo> ModLoaders { get; set; } = new();
    }

    public class CurseForgeModLoaderInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = ""; // e.g. "fabric-0.15.11" or "forge-47.2.0"

        [JsonPropertyName("primary")]
        public bool Primary { get; set; } = true;
    }

    public class CurseForgeFileItem
    {
        [JsonPropertyName("projectID")]
        public long ProjectId { get; set; }

        [JsonPropertyName("fileID")]
        public long FileId { get; set; }

        [JsonPropertyName("required")]
        public bool Required { get; set; } = true;
    }
}
