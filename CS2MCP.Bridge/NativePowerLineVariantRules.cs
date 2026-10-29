namespace CS2MCP
{
    // Native NetToolSystem switches to PlaceableNetData.m_UndergroundPrefab
    // when drawing below the surface. Accepting its range alone does not
    // switch the CreationDefinition's prefab or produce a buried cable.
    internal static class NativePowerLineVariantRules
    {
        internal static bool SelectUnderground(bool powerLine, bool nativeVariantAvailable,
            float surfaceMinimum, float startElevation, float endElevation)
            => powerLine && nativeVariantAvailable
                && (startElevation < surfaceMinimum || endElevation < surfaceMinimum);

        internal static bool FreeEndFits(bool snapped, float elevation, float minimum, float maximum)
            => snapped || (elevation >= minimum && elevation <= maximum);
    }
}
