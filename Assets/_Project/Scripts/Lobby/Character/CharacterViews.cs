using Noxy.Core;
using UnityEngine;

namespace Noxy.Lobby
{
    /// <summary>
    /// Anything that can display the player's character in the lobby. The placeholder implementation
    /// below is built from primitives; PrefabCharacterView swaps in real art without touching UI code.
    /// </summary>
    public interface ICharacterView
    {
        Transform Root { get; }
        void Apply(Loadout loadout, ICosmeticCatalog catalog);
    }

    public static class CharacterViewFactory
    {
        /// <summary>Resources path for the real lobby character prefab. Drop one in to replace the placeholder.</summary>
        public const string PrefabResourcePath = "Characters/LobbyCharacter";

        public static ICharacterView Create(Transform parent)
        {
            var prefab = Resources.Load<GameObject>(PrefabResourcePath);
            var go = new GameObject("Character");
            go.transform.SetParent(parent, false);

            if (prefab != null)
            {
                var view = go.AddComponent<PrefabCharacterView>();
                view.Setup(prefab);
                return view;
            }
            return go.AddComponent<PlaceholderCharacterView>();
        }
    }

    /// <summary>
    /// Uses an authored prefab (with its own Animator). Cosmetics are shown by toggling child objects
    /// whose names equal the cosmetic id (e.g. a child called "outfit_urban").
    /// </summary>
    public sealed class PrefabCharacterView : MonoBehaviour, ICharacterView
    {
        private readonly System.Collections.Generic.Dictionary<string, Transform> byName = new System.Collections.Generic.Dictionary<string, Transform>();

        public Transform Root => transform;

        public void Setup(GameObject prefab)
        {
            var instance = Instantiate(prefab, transform);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            foreach (var t in instance.GetComponentsInChildren<Transform>(true)) byName[t.name] = t;
        }

        public void Apply(Loadout loadout, ICosmeticCatalog catalog)
        {
            foreach (var item in catalog.Items)
            {
                if (!byName.TryGetValue(item.Id, out var node)) continue;
                node.gameObject.SetActive(loadout.Get(item.Slot) == item.Id);
            }
        }
    }

    /// <summary>Procedural stand-in character (primitives + code-driven idle animation).</summary>
    public sealed class PlaceholderCharacterView : MonoBehaviour, ICharacterView
    {
        private static readonly Color Skin = new Color(0.86f, 0.66f, 0.52f);
        private static readonly Color Hair = new Color(0.07f, 0.06f, 0.06f);

        private Transform model, pelvis, torso, head, shoulderL, shoulderR, elbowL, elbowR, hipL, hipR, kneeL, kneeR;
        private Material bodyMat, trimMat, legMat, shoeMat, shoeTrimMat;
        private GameObject maskObj, headgearObj, packObj;
        private float torsoBaseScaleY;
        private float pelvisBaseY;
        private readonly System.Collections.Generic.List<Material> dynamicMaterials = new System.Collections.Generic.List<Material>();

        public Transform Root => transform;

        private void Awake() => Build();

        private void Build()
        {
            model = new GameObject("Model").transform;
            model.SetParent(transform, false);
            model.localScale = Vector3.one * 0.92f;

            var skin = MaterialFactory.Lit(Skin, 0f, 0.25f);
            var hair = MaterialFactory.Lit(Hair, 0f, 0.4f);
            var eye = MaterialFactory.Unlit(new Color(0.03f, 0.03f, 0.04f));
            bodyMat = MaterialFactory.Lit(Color.gray, 0.05f, 0.35f);
            trimMat = MaterialFactory.Lit(Color.white, 0.1f, 0.5f);
            legMat = MaterialFactory.Lit(Color.gray, 0.05f, 0.3f);
            shoeMat = MaterialFactory.Lit(Color.gray, 0.1f, 0.4f);
            shoeTrimMat = MaterialFactory.Lit(Color.white, 0.1f, 0.5f);

            pelvis = new GameObject("Pelvis").transform;
            pelvis.SetParent(model, false);
            pelvis.localPosition = new Vector3(0f, 1.04f, 0f);
            pelvisBaseY = pelvis.localPosition.y;

            MaterialFactory.Primitive(PrimitiveType.Cube, "Hips", pelvis, new Vector3(0f, -0.04f, 0f), new Vector3(0.40f, 0.2f, 0.24f), legMat);
            MaterialFactory.Primitive(PrimitiveType.Cube, "Belt", pelvis, new Vector3(0f, 0.07f, 0f), new Vector3(0.43f, 0.06f, 0.26f), trimMat);

            torso = MaterialFactory.Primitive(PrimitiveType.Capsule, "Torso", pelvis, new Vector3(0f, 0.34f, 0f), new Vector3(0.44f, 0.27f, 0.27f), bodyMat).transform;
            torsoBaseScaleY = torso.localScale.y;
            MaterialFactory.Primitive(PrimitiveType.Cube, "ChestTrim", pelvis, new Vector3(0f, 0.4f, 0.125f), new Vector3(0.30f, 0.07f, 0.04f), trimMat);

            head = new GameObject("Head").transform;
            head.SetParent(pelvis, false);
            head.localPosition = new Vector3(0f, 0.68f, 0f);
            MaterialFactory.Primitive(PrimitiveType.Sphere, "Skull", head, new Vector3(0f, 0.11f, 0f), new Vector3(0.26f, 0.29f, 0.27f), skin);
            MaterialFactory.Primitive(PrimitiveType.Sphere, "Hair", head, new Vector3(0f, 0.19f, -0.015f), new Vector3(0.275f, 0.16f, 0.285f), hair);
            MaterialFactory.Primitive(PrimitiveType.Cube, "EyeL", head, new Vector3(-0.055f, 0.13f, 0.128f), new Vector3(0.04f, 0.03f, 0.02f), eye);
            MaterialFactory.Primitive(PrimitiveType.Cube, "EyeR", head, new Vector3(0.055f, 0.13f, 0.128f), new Vector3(0.04f, 0.03f, 0.02f), eye);

            BuildArm(-1f, skin, out shoulderL, out elbowL);
            BuildArm(1f, skin, out shoulderR, out elbowR);
            BuildLeg(-1f, skin, out hipL, out kneeL);
            BuildLeg(1f, skin, out hipR, out kneeR);

            Apply(DefaultCosmeticCatalog.StartingLoadout(), new DefaultCosmeticCatalog());
        }

        private void BuildArm(float side, Material skin, out Transform shoulder, out Transform elbow)
        {
            shoulder = new GameObject(side < 0 ? "ShoulderL" : "ShoulderR").transform;
            shoulder.SetParent(pelvis, false);
            shoulder.localPosition = new Vector3(0.29f * side, 0.56f, 0f);
            MaterialFactory.Primitive(PrimitiveType.Sphere, "ShoulderCap", shoulder, Vector3.zero, new Vector3(0.17f, 0.17f, 0.17f), bodyMat);
            MaterialFactory.Primitive(PrimitiveType.Capsule, "UpperArm", shoulder, new Vector3(0f, -0.19f, 0f), new Vector3(0.12f, 0.19f, 0.12f), bodyMat);

            elbow = new GameObject("Elbow").transform;
            elbow.SetParent(shoulder, false);
            elbow.localPosition = new Vector3(0f, -0.38f, 0f);
            MaterialFactory.Primitive(PrimitiveType.Capsule, "Forearm", elbow, new Vector3(0f, -0.17f, 0f), new Vector3(0.1f, 0.17f, 0.1f), trimMat);
            MaterialFactory.Primitive(PrimitiveType.Sphere, "Hand", elbow, new Vector3(0f, -0.37f, 0f), new Vector3(0.1f, 0.1f, 0.1f), skin);
        }

        private void BuildLeg(float side, Material skin, out Transform hip, out Transform knee)
        {
            hip = new GameObject(side < 0 ? "HipL" : "HipR").transform;
            hip.SetParent(pelvis, false);
            hip.localPosition = new Vector3(0.11f * side, -0.1f, 0f);
            MaterialFactory.Primitive(PrimitiveType.Capsule, "Thigh", hip, new Vector3(0f, -0.22f, 0f), new Vector3(0.17f, 0.22f, 0.17f), legMat);

            knee = new GameObject("Knee").transform;
            knee.SetParent(hip, false);
            knee.localPosition = new Vector3(0f, -0.44f, 0f);
            MaterialFactory.Primitive(PrimitiveType.Capsule, "Shin", knee, new Vector3(0f, -0.2f, 0f), new Vector3(0.14f, 0.2f, 0.14f), legMat);
            MaterialFactory.Primitive(PrimitiveType.Cube, "Shoe", knee, new Vector3(0f, -0.43f, 0.05f), new Vector3(0.16f, 0.1f, 0.3f), shoeMat);
            MaterialFactory.Primitive(PrimitiveType.Cube, "Sole", knee, new Vector3(0f, -0.485f, 0.05f), new Vector3(0.17f, 0.03f, 0.31f), shoeTrimMat);
        }

        public void Apply(Loadout loadout, ICosmeticCatalog catalog)
        {
            if (loadout == null || catalog == null) return;

            if (catalog.TryGet(loadout.outfit, out var outfit))
            {
                MaterialFactory.SetColor(bodyMat, outfit.PrimaryColor);
                MaterialFactory.SetColor(legMat, outfit.PrimaryColor * 0.75f);
                MaterialFactory.SetColor(trimMat, outfit.SecondaryColor);
            }
            if (catalog.TryGet(loadout.shoes, out var shoes))
            {
                MaterialFactory.SetColor(shoeMat, shoes.PrimaryColor);
                MaterialFactory.SetColor(shoeTrimMat, shoes.SecondaryColor);
            }

            ClearDynamic();
            catalog.TryGet(loadout.mask, out var mask);
            catalog.TryGet(loadout.headgear, out var headgear);
            catalog.TryGet(loadout.backpack, out var pack);
            if (mask != null) BuildMask(mask);
            if (headgear != null) BuildHeadgear(headgear);
            if (pack != null) BuildBackpack(pack);
        }

        private void ClearDynamic()
        {
            if (maskObj != null) Destroy(maskObj);
            if (headgearObj != null) Destroy(headgearObj);
            if (packObj != null) Destroy(packObj);
            foreach (var m in dynamicMaterials) if (m != null) Destroy(m);
            dynamicMaterials.Clear();
        }

        private Material Lit(Color c, float metallic = 0.1f, float smooth = 0.45f)
        {
            var m = MaterialFactory.Lit(c, metallic, smooth);
            dynamicMaterials.Add(m);
            return m;
        }

        private Material Glow(Color c)
        {
            var m = MaterialFactory.Unlit(c);
            dynamicMaterials.Add(m);
            return m;
        }

        private void BuildMask(CosmeticItem item)
        {
            maskObj = new GameObject("Mask");
            maskObj.transform.SetParent(head, false);
            var t = maskObj.transform;
            var primary = Lit(item.PrimaryColor);
            var secondary = Lit(item.SecondaryColor);

            switch (item.Variant)
            {
                case 1: // visor
                    MaterialFactory.Primitive(PrimitiveType.Cube, "Strap", t, new Vector3(0f, 0.13f, 0f), new Vector3(0.285f, 0.05f, 0.29f), primary);
                    MaterialFactory.Primitive(PrimitiveType.Cube, "Lens", t, new Vector3(0f, 0.13f, 0.128f), new Vector3(0.27f, 0.07f, 0.05f), Glow(item.SecondaryColor));
                    break;
                case 2: // filter mask
                    MaterialFactory.Primitive(PrimitiveType.Cube, "Mouthpiece", t, new Vector3(0f, 0.04f, 0.115f), new Vector3(0.2f, 0.12f, 0.08f), primary);
                    var filter = MaterialFactory.Primitive(PrimitiveType.Cylinder, "Filter", t, new Vector3(0f, 0.03f, 0.17f), new Vector3(0.09f, 0.03f, 0.09f), secondary);
                    filter.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    break;
                case 3: // bone mask
                    MaterialFactory.Primitive(PrimitiveType.Sphere, "Plate", t, new Vector3(0f, 0.09f, 0.075f), new Vector3(0.25f, 0.23f, 0.12f), primary);
                    MaterialFactory.Primitive(PrimitiveType.Cube, "SocketL", t, new Vector3(-0.05f, 0.12f, 0.138f), new Vector3(0.05f, 0.05f, 0.02f), secondary);
                    MaterialFactory.Primitive(PrimitiveType.Cube, "SocketR", t, new Vector3(0.05f, 0.12f, 0.138f), new Vector3(0.05f, 0.05f, 0.02f), secondary);
                    MaterialFactory.Primitive(PrimitiveType.Cube, "Teeth", t, new Vector3(0f, 0.03f, 0.142f), new Vector3(0.13f, 0.03f, 0.02f), secondary);
                    break;
                default: // bandana
                    MaterialFactory.Primitive(PrimitiveType.Cube, "Cloth", t, new Vector3(0f, 0.04f, 0.105f), new Vector3(0.25f, 0.11f, 0.1f), primary);
                    MaterialFactory.Primitive(PrimitiveType.Cube, "Knot", t, new Vector3(0f, 0.09f, 0.0f), new Vector3(0.275f, 0.03f, 0.285f), secondary);
                    break;
            }
        }

        private void BuildHeadgear(CosmeticItem item)
        {
            headgearObj = new GameObject("Headgear");
            headgearObj.transform.SetParent(head, false);
            var t = headgearObj.transform;
            var primary = Lit(item.PrimaryColor, item.Rarity == CosmeticRarity.Legendary ? 0.8f : 0.1f, item.Rarity == CosmeticRarity.Legendary ? 0.8f : 0.4f);
            var secondary = Lit(item.SecondaryColor);

            switch (item.Variant)
            {
                case 1: // helmet
                    MaterialFactory.Primitive(PrimitiveType.Sphere, "Shell", t, new Vector3(0f, 0.18f, 0f), new Vector3(0.33f, 0.27f, 0.34f), primary);
                    MaterialFactory.Primitive(PrimitiveType.Cube, "Crest", t, new Vector3(0f, 0.3f, 0f), new Vector3(0.04f, 0.04f, 0.3f), secondary);
                    MaterialFactory.Primitive(PrimitiveType.Cube, "Rim", t, new Vector3(0f, 0.145f, 0.15f), new Vector3(0.3f, 0.025f, 0.07f), secondary);
                    break;
                case 2: // beanie
                    MaterialFactory.Primitive(PrimitiveType.Sphere, "Dome", t, new Vector3(0f, 0.19f, 0f), new Vector3(0.3f, 0.2f, 0.31f), primary);
                    MaterialFactory.Primitive(PrimitiveType.Cube, "Cuff", t, new Vector3(0f, 0.125f, 0f), new Vector3(0.305f, 0.045f, 0.315f), secondary);
                    MaterialFactory.Primitive(PrimitiveType.Sphere, "Pom", t, new Vector3(0f, 0.31f, 0f), new Vector3(0.07f, 0.07f, 0.07f), secondary);
                    break;
                default: // cap
                    MaterialFactory.Primitive(PrimitiveType.Sphere, "Dome", t, new Vector3(0f, 0.2f, 0f), new Vector3(0.29f, 0.17f, 0.3f), primary);
                    MaterialFactory.Primitive(PrimitiveType.Cube, "Brim", t, new Vector3(0f, 0.155f, 0.165f), new Vector3(0.22f, 0.02f, 0.14f), secondary);
                    break;
            }
        }

        private void BuildBackpack(CosmeticItem item)
        {
            packObj = new GameObject("Backpack");
            packObj.transform.SetParent(pelvis, false);
            var t = packObj.transform;
            var primary = Lit(item.PrimaryColor);
            var secondary = Lit(item.SecondaryColor);

            if (item.Variant == 1)
            {
                MaterialFactory.Primitive(PrimitiveType.Cube, "Rig", t, new Vector3(0f, 0.32f, -0.2f), new Vector3(0.4f, 0.5f, 0.2f), primary);
                MaterialFactory.Primitive(PrimitiveType.Cube, "PouchL", t, new Vector3(-0.23f, 0.22f, -0.17f), new Vector3(0.1f, 0.18f, 0.12f), secondary);
                MaterialFactory.Primitive(PrimitiveType.Cube, "PouchR", t, new Vector3(0.23f, 0.22f, -0.17f), new Vector3(0.1f, 0.18f, 0.12f), secondary);
            }
            else
            {
                MaterialFactory.Primitive(PrimitiveType.Cube, "Pack", t, new Vector3(0f, 0.3f, -0.2f), new Vector3(0.34f, 0.42f, 0.16f), primary);
                MaterialFactory.Primitive(PrimitiveType.Cube, "Flap", t, new Vector3(0f, 0.48f, -0.2f), new Vector3(0.35f, 0.08f, 0.17f), secondary);
            }
        }

        private void Update()
        {
            float t = Time.unscaledTime;
            float breath = Mathf.Sin(t * 1.6f);

            pelvis.localPosition = new Vector3(0f, pelvisBaseY + 0.006f * breath, 0f);
            pelvis.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(t * 0.5f) * 1.2f);
            torso.localScale = new Vector3(torso.localScale.x, torsoBaseScaleY * (1f + 0.014f * breath), torso.localScale.z);

            head.localRotation = Quaternion.Euler(Mathf.Sin(t * 0.7f) * 2f, Mathf.Sin(t * 0.45f + 1f) * 6f, -Mathf.Sin(t * 0.5f) * 1.2f);

            shoulderL.localRotation = Quaternion.Euler(Mathf.Sin(t * 0.9f) * 2.5f, 0f, -8f + Mathf.Sin(t * 1.6f + 0.4f) * 1.5f);
            shoulderR.localRotation = Quaternion.Euler(Mathf.Sin(t * 0.9f + 1.4f) * 2.5f, 0f, 8f - Mathf.Sin(t * 1.6f + 0.4f) * 1.5f);
            elbowL.localRotation = Quaternion.Euler(-14f + Mathf.Sin(t * 1.1f) * 2f, 0f, 0f);
            elbowR.localRotation = Quaternion.Euler(-14f + Mathf.Sin(t * 1.1f + 1f) * 2f, 0f, 0f);

            hipL.localRotation = Quaternion.Euler(0f, -4f, 2f + Mathf.Sin(t * 0.5f) * 0.8f);
            hipR.localRotation = Quaternion.Euler(0f, 4f, -2f + Mathf.Sin(t * 0.5f) * 0.8f);
            kneeL.localRotation = Quaternion.identity;
            kneeR.localRotation = Quaternion.identity;
        }

        private void OnDestroy()
        {
            ClearDynamic();
        }
    }
}
