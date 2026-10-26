using System;
using System.Globalization;
using System.Text;

namespace Noxy.Core
{
    /// <summary>
    /// Writes the island as compact JSON for the match server, so the server validates landings, loot pickups and positions
    /// against the same map the client renders. The heightmap is coarse (129 x 129): the server needs "is this land / how high",
    /// not render detail. Regenerate with NOXY_WRITE_MAP=1 (see tools/core-tests/MapExportTests.cs); a test fails if the
    /// committed file is stale.
    /// </summary>
    public static class IslandExport
    {
        public const int ExportGrid = 129;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static string F(float v) => v.ToString("0.#", Inv);
        private static string S(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        public static string ToJson(IslandLayout l)
        {
            var sb = new StringBuilder(260000);
            sb.Append("{\"mapId\":\"bermuda\",\"seed\":").Append(l.Seed.ToString(Inv));
            sb.Append(",\"worldSize\":").Append(F(l.WorldSize)).Append(",\"waterLevel\":").Append(F(l.WaterLevel));
            sb.Append(",\"grid\":").Append(ExportGrid).Append(",\"heightsDecimeters\":[");
            float step = l.WorldSize / (ExportGrid - 1);
            for (int z = 0; z < ExportGrid; z++)
                for (int x = 0; x < ExportGrid; x++)
                {
                    if (z + x > 0) sb.Append(',');
                    sb.Append(((int)Math.Round(l.HeightAt(-l.Half + x * step, -l.Half + z * step) * 10f)).ToString(Inv));
                }
            sb.Append("],\"pois\":[");
            for (int i = 0; i < l.Pois.Count; i++)
            {
                var p = l.Pois[i]; if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(S(p.Id)).Append(",\"name\":").Append(S(p.Name)).Append(",\"kind\":").Append(S(p.Kind.ToString().ToLowerInvariant()))
                  .Append(",\"landmark\":").Append(S(p.LandmarkSlot)).Append(",\"x\":").Append(F(p.X)).Append(",\"z\":").Append(F(p.Z))
                  .Append(",\"radius\":").Append(F(p.Radius)).Append(",\"padHeight\":").Append(F(p.PadHeight)).Append(",\"lootTier\":").Append(p.LootTier).Append('}');
            }
            sb.Append("],\"landingZones\":[");
            for (int i = 0; i < l.LandingZones.Count; i++)
            {
                var z = l.LandingZones[i]; if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(S(z.Id)).Append(",\"name\":").Append(S(z.Name)).Append(",\"x\":").Append(F(z.X)).Append(",\"z\":").Append(F(z.Z)).Append(",\"radius\":").Append(F(z.Radius)).Append('}');
            }
            sb.Append("],\"buildings\":[");
            for (int i = 0; i < l.Buildings.Count; i++)
            {
                var b = l.Buildings[i]; if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(S(b.Id)).Append(",\"poi\":").Append(S(b.PoiId)).Append(",\"x\":").Append(F(b.X)).Append(",\"z\":").Append(F(b.Z))
                  .Append(",\"yaw\":").Append(F(b.Yaw)).Append(",\"w\":").Append(F(b.Width)).Append(",\"d\":").Append(F(b.Depth)).Append(",\"baseY\":").Append(F(b.BaseY))
                  .Append(",\"floors\":").Append(b.Floors).Append(",\"interior\":").Append(b.HasInterior ? "true" : "false").Append('}');
            }
            sb.Append("],\"loot\":[");
            for (int i = 0; i < l.Loot.Count; i++)
            {
                var s = l.Loot[i]; if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(S(s.Id)).Append(",\"building\":").Append(S(s.BuildingId)).Append(",\"kind\":").Append(S(s.Kind))
                  .Append(",\"tier\":").Append(s.Tier).Append(",\"x\":").Append(F(s.X)).Append(",\"y\":").Append(F(s.Y)).Append(",\"z\":").Append(F(s.Z)).Append('}');
            }
            sb.Append("],\"roads\":[");
            for (int i = 0; i < l.Roads.Count; i++)
            {
                var r = l.Roads[i]; if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(S(r.Id)).Append(",\"from\":").Append(S(r.FromPoi)).Append(",\"to\":").Append(S(r.ToPoi)).Append(",\"points\":[");
                for (int k = 0; k < r.Points.Count; k++) { if (k > 0) sb.Append(','); sb.Append('[').Append(F(r.Points[k].X)).Append(',').Append(F(r.Points[k].Z)).Append(']'); }
                sb.Append("]}");
            }
            sb.Append("],\"bridges\":[");
            for (int i = 0; i < l.Bridges.Count; i++)
            {
                var b = l.Bridges[i]; if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(S(b.Id)).Append(",\"x\":").Append(F(b.X)).Append(",\"z\":").Append(F(b.Z)).Append(",\"yaw\":").Append(F(b.Yaw))
                  .Append(",\"length\":").Append(F(b.Length)).Append(",\"width\":").Append(F(b.Width)).Append(",\"deckY\":").Append(F(b.DeckY)).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
