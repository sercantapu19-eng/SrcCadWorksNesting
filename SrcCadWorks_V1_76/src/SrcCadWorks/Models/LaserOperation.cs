using System;
using System.Linq;

namespace SrcCadWorks.Models
{
    public static class LaserOperation
    {
        public static string[] Choices => new[]
        {
            "KESİM", "KESİM+BÜKÜM", "KESİM+KILAVUZ", "KESİM+BÜKÜM+KILAVUZ",
            "KESİM+HAVŞA", "KESİM+BÜKÜM+HAVŞA", "KESİM+KILAVUZ+HAVŞA", "KESİM+BÜKÜM+KILAVUZ+HAVŞA"
        };

        // Exact process value, not a search through descriptions or unrelated properties.
        public static bool IsLaserProcessTag(string value)
        {
            return string.Equals((value ?? "").Trim(), "LAZER", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsWeldProcessTag(string value)
        {
            return string.Equals((value ?? "").Trim(), "KAYNAK", StringComparison.OrdinalIgnoreCase);
        }

        public static bool MatchesProcessFilter(bool isLaserTagged, bool isWeldTagged, bool includeLaser, bool includeWeld)
        {
            // With neither category selected, show every sheet-metal row in the scan scope.
            return (!includeLaser && !includeWeld)
                || (includeLaser && isLaserTagged) || (includeWeld && isWeldTagged);
        }

        public static string Build(bool hasBends, bool hasTappedHoles, bool hasCountersink)
        {
            return "KESİM" + (hasBends ? "+BÜKÜM" : "")
                + (hasTappedHoles ? "+KILAVUZ" : "") + (hasCountersink ? "+HAVŞA" : "");
        }

        public static string WithCountersink(string operation)
        {
            if (string.IsNullOrWhiteSpace(operation)) return "KESİM+HAVŞA";
            if (operation.Split('+').Any(x => string.Equals(x.Trim(), "HAVŞA", StringComparison.OrdinalIgnoreCase)))
                return operation;
            return operation + "+HAVŞA";
        }

        // Input is Enum.GetName(swWzdHoleTypes_e, IWizardHoleFeatureData2.Type),
        // never a user-editable feature name. Slot variants use the same hole types.
        public static bool IsCountersinkHoleType(string typeName, bool suppressed = false)
        {
            if (suppressed || string.IsNullOrWhiteSpace(typeName)
                || !typeName.StartsWith("sw", StringComparison.OrdinalIgnoreCase)) return false;
            return typeName.IndexOf("CounterSink", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("CounterSunk", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("CounterBore", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.StartsWith("swCounterDrill", StringComparison.OrdinalIgnoreCase);
        }
    }
}
