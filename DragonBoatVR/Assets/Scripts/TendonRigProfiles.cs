using System;
using System.IO;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Explicit CAD design input for the existing software controller.
    /// Registration and guide-role assumptions remain in the bundled provenance.</summary>
    public static class TendonRigProfiles
    {
        public const string CadArgument = "-cad-tendon-rig";
        public const string CadFileName = "tendon_rig_cad.json";
        public static bool CadDesignRequested => Array.IndexOf(Environment.GetCommandLineArgs(), CadArgument) >= 0;

        public static TendonRigConfig LoadCadDesign()
        {
            string path = Path.Combine(Application.streamingAssetsPath, CadFileName);
            var profile = JsonUtility.FromJson<TendonRigConfig>(File.ReadAllText(path));
            if (profile == null || profile.tendons == null || profile.tendons.Length != 3
                || profile.measured || !profile.geometryAssumed || !profile.attachmentCoordinatesFromBladeTip
                || string.IsNullOrEmpty(profile.provenance) || !profile.provenance.StartsWith("CAD_DESIGN_", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid CAD design profile; refusing an implicit example-rig fallback.");
            return profile;
        }
    }
}
