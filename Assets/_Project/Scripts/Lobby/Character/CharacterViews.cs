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

        /// <summary>Plays the emote animation (variant comes from the cosmetic's Variant).</summary>
        void PlayEmote(int variant);

        /// <summary>Shows the weapon prop for the selected weapon slot (0 primary, 1 secondary, 2 melee).</summary>
        void SetHeldWeapon(int slot);
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

        public void PlayEmote(int variant)
        {
            var animator = GetComponentInChildren<Animator>();
            if (animator == null) return;
            string trigger = "Emote" + variant;
            foreach (var p in animator.parameters)
                if (p.name == trigger) { animator.SetTrigger(trigger); return; }
        }

        public void SetHeldWeapon(int slot)
        {
            var animator = GetComponentInChildren<Animator>();
            if (animator != null) foreach (var p in animator.parameters)
                if (p.name == "WeaponSlot") { animator.SetInteger("WeaponSlot", slot); return; }
        }
    }

    /// <summary>Original Noxy Pathfinder lobby character, built from primitives with a code-driven idle animation.</summary>
    public sealed class PlaceholderCharacterView : MonoBehaviour, ICharacterView
    {
        private static readonly Color Skin = new Color(0.86f, 0.66f, 0.52f);
        private static readonly Color Hair = new Color(0.07f, 0.06f, 0.06f);

        private Transform model, pelvis, torso, head, shoulderL, shoulderR, elbowL, elbowR, hipL, hipR, kneeL, kneeR;
        private Material bodyMat, trimMat, legMat, shoeMat, shoeTrimMat;
        private Material kitMat, signalMat;
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
            kitMat = MaterialFactory.Lit(new Color(0.08f, 0.13f, 0.17f), 0.25f, 0.45f);
            signalMat = MaterialFactory.Unlit(new Color(0.12f, 0.92f, 0.78f));

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
            BuildPathfinderDetails();

            Apply(DefaultCosmeticCatalog.StartingLoadout(), new DefaultCosmeticCatalog());
        }

        private void BuildPathfinderDetails()
        {
            MaterialFactory.Primitive(PrimitiveType.Cube, "ChestRig", pelvis, new Vector3(0f, 0.34f, 0.145f), new Vector3(0.29f, 0.2f, 0.075f), kitMat);
            MaterialFactory.Primitive(PrimitiveType.Cube, "ChestSignal", pelvis, new Vector3(0f, 0.34f, 0.186f), new Vector3(0.055f, 0.12f, 0.012f), signalMat);
            MaterialFactory.Primitive(PrimitiveType.Cube, "ChestPocketL", pelvis, new Vector3(-0.095f, 0.24f, 0.17f), new Vector3(0.075f, 0.07f, 0.035f), trimMat);
            MaterialFactory.Primitive(PrimitiveType.Cube, "ChestPocketR", pelvis, new Vector3(0.095f, 0.24f, 0.17f), new Vector3(0.075f, 0.07f, 0.035f), trimMat);

            MaterialFactory.Primitive(PrimitiveType.Cube, "ShoulderPlateL", shoulderL, new Vector3(-0.015f, 0.055f, 0.025f), new Vector3(0.18f, 0.07f, 0.18f), kitMat);
            MaterialFactory.Primitive(PrimitiveType.Cube, "ShoulderMarkL", shoulderL, new Vector3(-0.015f, 0.094f, 0.03f), new Vector3(0.11f, 0.018f, 0.11f), signalMat);
            MaterialFactory.Primitive(PrimitiveType.Cube, "ShoulderPlateR", shoulderR, new Vector3(0.015f, 0.055f, 0.025f), new Vector3(0.18f, 0.07f, 0.18f), kitMat);

            MaterialFactory.Primitive(PrimitiveType.Cube, "UtilityPouch", pelvis, new Vector3(0.23f, 0.03f, 0.04f), new Vector3(0.11f, 0.15f, 0.12f), kitMat);
            MaterialFactory.Primitive(PrimitiveType.Cube, "PouchLatch", pelvis, new Vector3(0.23f, 0.07f, 0.105f), new Vector3(0.045f, 0.035f, 0.015f), signalMat);
            MaterialFactory.Primitive(PrimitiveType.Cube, "KneePadL", hipL, new Vector3(0f, -0.42f, 0.09f), new Vector3(0.18f, 0.11f, 0.055f), kitMat);
            MaterialFactory.Primitive(PrimitiveType.Cube, "KneePadR", hipR, new Vector3(0f, -0.42f, 0.09f), new Vector3(0.18f, 0.11f, 0.055f), kitMat);

            MaterialFactory.Primitive(PrimitiveType.Sphere, "CommsEarpiece", head, new Vector3(-0.135f, 0.08f, 0f), new Vector3(0.07f, 0.11f, 0.1f), kitMat);
            MaterialFactory.Primitive(PrimitiveType.Cube, "CommsIndicator", head, new Vector3(-0.16f, 0.08f, 0.045f), new Vector3(0.018f, 0.035f, 0.025f), signalMat);
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
            var fallbackSkin = new CosmeticItem { Id = "", PrimaryColor = new Color(0.16f, 0.18f, 0.21f), SecondaryColor = new Color(0.42f, 0.45f, 0.5f) };
            BuildWeapon(catalog.TryGet(loadout.weaponSkin, out var weapon) ? weapon : fallbackSkin);
        }

        private GameObject weaponObj, sidearmObj, bladeObj;
        private float emoteTimer;
        private int emoteVariant;
        private int heldSlot;
        private const float EmoteSeconds = 2.6f;

        public void PlayEmote(int variant)
        {
            emoteVariant = variant;
            emoteTimer = EmoteSeconds;
        }

        public void SetHeldWeapon(int slot)
        {
            heldSlot = slot;
            if (weaponObj != null) weaponObj.SetActive(slot == 0);
            if (sidearmObj != null) sidearmObj.SetActive(slot == 1);
            if (bladeObj != null) bladeObj.SetActive(slot == 2);
        }

        private void BuildWeapon(CosmeticItem item)
        {
            var dark = Lit(new Color(0.08f, 0.08f, 0.1f));

            // Sidearm and blade use fixed colors: the weapon skin applies to the primary rifle.
            sidearmObj = new GameObject("Sidearm");
            sidearmObj.transform.SetParent(pelvis, false);
            sidearmObj.transform.localPosition = new Vector3(0.2f, 0.28f, 0.22f);
            sidearmObj.transform.localRotation = Quaternion.Euler(-15f, 5f, 0f);
            MaterialFactory.Primitive(PrimitiveType.Cube, "Slide", sidearmObj.transform, new Vector3(0f, 0.02f, 0.08f), new Vector3(0.05f, 0.06f, 0.24f), dark);
            MaterialFactory.Primitive(PrimitiveType.Cube, "Grip", sidearmObj.transform, new Vector3(0f, -0.07f, 0f), new Vector3(0.045f, 0.12f, 0.06f), Lit(new Color(0.2f, 0.2f, 0.24f)));

            bladeObj = new GameObject("Blade");
            bladeObj.transform.SetParent(pelvis, false);
            bladeObj.transform.localPosition = new Vector3(0.2f, 0.28f, 0.22f);
            bladeObj.transform.localRotation = Quaternion.Euler(-70f, 0f, 0f);
            MaterialFactory.Primitive(PrimitiveType.Cube, "Edge", bladeObj.transform, new Vector3(0f, 0f, 0.14f), new Vector3(0.025f, 0.07f, 0.3f), Lit(new Color(0.75f, 0.78f, 0.82f), 0.9f, 0.8f));
            MaterialFactory.Primitive(PrimitiveType.Cube, "Handle", bladeObj.transform, new Vector3(0f, 0f, -0.06f), new Vector3(0.04f, 0.05f, 0.12f), dark);

            weaponObj = new GameObject("Weapon");
            weaponObj.transform.SetParent(pelvis, false);
            weaponObj.transform.localPosition = new Vector3(0.12f, 0.3f, 0.26f);
            weaponObj.transform.localRotation = Quaternion.Euler(-25f, 8f, 0f);
            var t = weaponObj.transform;
            var body = Lit(item.PrimaryColor, item.Rarity == CosmeticRarity.Legendary ? 0.9f : 0.2f, 0.55f);
            var accent = Lit(item.SecondaryColor, 0.2f, 0.5f);

            MaterialFactory.Primitive(PrimitiveType.Cube, "Receiver", t, new Vector3(0f, 0f, 0f), new Vector3(0.07f, 0.1f, 0.5f), body);
            MaterialFactory.Primitive(PrimitiveType.Cube, "Handguard", t, new Vector3(0f, 0.005f, 0.34f), new Vector3(0.065f, 0.075f, 0.24f), accent);
            var barrel = MaterialFactory.Primitive(PrimitiveType.Cylinder, "Barrel", t, new Vector3(0f, 0.01f, 0.58f), new Vector3(0.03f, 0.14f, 0.03f), dark);
            barrel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            MaterialFactory.Primitive(PrimitiveType.Cube, "Magazine", t, new Vector3(0f, -0.14f, 0.06f), new Vector3(0.05f, 0.2f, 0.09f), dark);
            MaterialFactory.Primitive(PrimitiveType.Cube, "Stock", t, new Vector3(0f, -0.02f, -0.32f), new Vector3(0.06f, 0.14f, 0.22f), accent);
            MaterialFactory.Primitive(PrimitiveType.Cube, "Sight", t, new Vector3(0f, 0.075f, 0.1f), new Vector3(0.03f, 0.04f, 0.12f), dark);

            SetHeldWeapon(heldSlot);
        }

        private void ClearDynamic()
        {
            if (maskObj != null) Destroy(maskObj);
            if (headgearObj != null) Destroy(headgearObj);
            if (packObj != null) Destroy(packObj);
            if (weaponObj != null) Destroy(weaponObj);
            if (sidearmObj != null) Destroy(sidearmObj);
            if (bladeObj != null) Destroy(bladeObj);
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

            if (emoteTimer > 0f) AnimateEmote(t);
        }

        private void AnimateEmote(float t)
        {
            emoteTimer -= Time.unscaledDeltaTime;
            float p = 1f - Mathf.Clamp01(emoteTimer / EmoteSeconds);        // 0 -> 1 over the emote
            float blend = Mathf.Clamp01(Mathf.Min(p, 1f - p) * 6f);          // ease in and out
            switch (emoteVariant)
            {
                case 1: // flex
                    shoulderL.localRotation = Quaternion.Slerp(shoulderL.localRotation, Quaternion.Euler(0f, 0f, -75f), blend);
                    shoulderR.localRotation = Quaternion.Slerp(shoulderR.localRotation, Quaternion.Euler(0f, 0f, 75f), blend);
                    elbowL.localRotation = Quaternion.Slerp(elbowL.localRotation, Quaternion.Euler(0f, 0f, 110f + Mathf.Sin(t * 9f) * 6f), blend);
                    elbowR.localRotation = Quaternion.Slerp(elbowR.localRotation, Quaternion.Euler(0f, 0f, -110f - Mathf.Sin(t * 9f) * 6f), blend);
                    break;
                case 2: // shuffle
                    pelvis.localRotation = Quaternion.Euler(0f, Mathf.Sin(t * 8f) * 14f, Mathf.Sin(t * 4f) * 4f);
                    shoulderL.localRotation = Quaternion.Euler(Mathf.Sin(t * 8f) * 35f, 0f, -20f);
                    shoulderR.localRotation = Quaternion.Euler(-Mathf.Sin(t * 8f) * 35f, 0f, 20f);
                    hipL.localRotation = Quaternion.Euler(Mathf.Sin(t * 8f) * 20f, 0f, 0f);
                    hipR.localRotation = Quaternion.Euler(-Mathf.Sin(t * 8f) * 20f, 0f, 0f);
                    break;
                case 3: // salute
                    shoulderR.localRotation = Quaternion.Slerp(shoulderR.localRotation, Quaternion.Euler(-30f, 0f, 125f), blend);
                    elbowR.localRotation = Quaternion.Slerp(elbowR.localRotation, Quaternion.Euler(0f, 0f, -125f), blend);
                    break;
                default: // wave
                    shoulderR.localRotation = Quaternion.Slerp(shoulderR.localRotation, Quaternion.Euler(0f, 0f, 150f), blend);
                    elbowR.localRotation = Quaternion.Slerp(elbowR.localRotation, Quaternion.Euler(0f, 0f, Mathf.Sin(t * 12f) * 28f), blend);
                    break;
            }
        }

        private void OnDestroy()
        {
            ClearDynamic();
        }
    }
}
