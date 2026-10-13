using System.Collections.Generic;
using System.Text.Json;

namespace MegaCrit.Sts2.Core.Assets
{
    public class TpSheetData
    {
        public List<TpSheetTexture> Textures { get; set; }
    }

    public class TpSheetTexture
    {
        public string Image { get; set; }
        public TpSheetSize Size { get; set; }
        public List<TpSheetSprite> Sprites { get; set; }
    }

    public class TpSheetSprite
    {
        public string Filename { get; set; }
        public TpSheetRect Region { get; set; }
    }

    public struct TpSheetRect
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
    }

    public struct TpSheetSize
    {
        public int W { get; set; }
        public int H { get; set; }
    }

    public static class AtlasManager
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public static TpSheetData Load(string json) => JsonSerializer.Deserialize<TpSheetData>(json, _jsonOptions);
    }
}
