using System;
using System.Security.Cryptography;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Displayed-hull clip data only. Does not change waves or forces.</summary>
    internal sealed class BoatInteriorWaterProfile : IDisposable
    {
        [Serializable] sealed class Description
        {
            public int schemaVersion, width, height;
            public string modelId, sourceObjSha256, dataSha256;
            public float minimumZ, maximumZ, minimumY, referenceY, minimumX, maximumX;
        }
        static readonly int Enabled = Shader.PropertyToID("_BoatInteriorEnabled");
        static readonly int WorldToLocal = Shader.PropertyToID("_BoatInteriorWorldToLocal");
        static readonly int Profile = Shader.PropertyToID("_BoatInteriorProfile");
        static readonly int Domain = Shader.PropertyToID("_BoatInteriorDomain");
        static readonly int Heights = Shader.PropertyToID("_BoatInteriorHeights");
        static readonly int Texel = Shader.PropertyToID("_BoatInteriorTexel");
        static BoatInteriorWaterProfile _published;
        readonly Transform _boat;
        readonly Description _description;
        readonly Texture2D _texture;
        readonly Renderer _obsoleteCap;
        readonly bool _capWasEnabled;

        public BoatInteriorWaterProfile(BoatModelChoice choice)
        {
            if (choice == null) throw new ArgumentNullException(nameof(choice));
            _boat = choice.transform;
            string resource = "Water/BoatInteriors/" + choice.modelId;
            var text = Resources.Load<TextAsset>(resource + "_profile");
            var data = Resources.Load<TextAsset>(resource);
            if (text == null || data == null) throw new InvalidOperationException("Missing boat interior water profile: " + choice.modelId);
            _description = JsonUtility.FromJson<Description>(text.text);
            var d = _description;
            if (d == null || d.schemaVersion != 1 || d.modelId != choice.modelId || d.width != 1024 || d.height != 64
                || !(d.maximumZ > d.minimumZ) || !(d.referenceY > d.minimumY)
                || !(d.maximumX > d.minimumX) || data.bytes.Length != d.width*d.height*8)
                throw new InvalidOperationException("Invalid boat interior water profile: " + choice.modelId);
            byte[] bytes = data.bytes;
            using (var hash = SHA256.Create())
                if (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != d.dataSha256)
                    throw new InvalidOperationException("Boat interior water profile bytes do not match their manifest.");
            var values = new float[bytes.Length/4]; Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            foreach (float value in values)
                if (!SimulationValue.Finite(value)) throw new InvalidOperationException("Nonfinite boat interior water profile.");
            _texture = new Texture2D(d.width, d.height, TextureFormat.RGFloat, false, true)
            {
                name = choice.modelId + " interior water clip",
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            _texture.LoadRawTextureData(bytes); _texture.Apply(false, true);
            var old = _boat.Find("InteriorWaterExclusion");
            _obsoleteCap = old != null ? old.GetComponent<Renderer>() : null;
            if (_obsoleteCap != null) { _capWasEnabled = _obsoleteCap.enabled; _obsoleteCap.enabled = false; }
        }

        public void Publish()
        {
            if (_boat == null) return;
            var d = _description;
            Shader.SetGlobalMatrix(WorldToLocal, _boat.worldToLocalMatrix);
            Shader.SetGlobalTexture(Profile, _texture);
            Shader.SetGlobalVector(Domain, new Vector4(d.minimumX, d.maximumX, d.minimumZ, d.maximumZ));
            Shader.SetGlobalVector(Heights, new Vector4(d.minimumY, d.referenceY, 0, 0));
            Shader.SetGlobalVector(Texel, new Vector4(1f/d.width, 1f/d.height, 0, 0));
            Shader.SetGlobalFloat(Enabled, 1f); _published = this;
        }

        public void Dispose()
        {
            if (_published == this) { Shader.SetGlobalFloat(Enabled, 0f); _published = null; }
            if (_obsoleteCap != null) _obsoleteCap.enabled = _capWasEnabled;
            if (_texture != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_texture);
                else UnityEngine.Object.DestroyImmediate(_texture);
            }
        }
    }
}
