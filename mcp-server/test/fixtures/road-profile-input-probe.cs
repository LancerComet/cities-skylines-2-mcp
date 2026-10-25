using System;
namespace CS2MCP
{
    public static class RoadProfileInputProbe
    {
        public static string Run()
        {
            var accepted = new[] { "x", "z", "rotation", "elevation", "dryRun", "segments", "preserveVerticalProfile" };
            var rejected = new[] { "force", "owner", "PreserveVerticalProfile", "ignoreErrors", "replaceRoads", "", null };
            foreach (var field in accepted) if (!RoadLayoutInputRules.IsKnownField(field)) throw new Exception("合法布局字段被拒：" + field);
            foreach (var field in rejected) if (RoadLayoutInputRules.IsKnownField(field)) throw new Exception("未知布局字段被接受：" + field);
            return "native_boundary_known_7_unknown_7_pass";
        }
    }
}
