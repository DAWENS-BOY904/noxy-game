using System.Collections.Generic;
using UnityEngine;

namespace Noxy.Lobby
{
    /// <summary>
    /// Cosmetic companion that floats next to a target. PLACEHOLDER art: primitive shapes per companion id until real
    /// models, LODs and animator controllers exist (see docs/CLANS_FAIRPLAY_SETUP.md, "Assets required").
    /// Purely visual: no colliders, no rigidbody, no network sync, so it cannot block movement or affect hit detection.
    /// Instances are pooled by companion id so equipping and unequipping does not allocate.
    /// </summary>
    public sealed class CompanionFollower : MonoBehaviour
    {
        private static readonly Dictionary<string, Stack<CompanionFollower>> Pool = new Dictionary<string, Stack<CompanionFollower>>();
        private static CompanionFollower active;

        private Transform target;
        private Vector3 offset = new Vector3(0.75f, 1.55f, 0.25f);
        private float phase;
        private Transform visual;
        private string companionId;
        private Renderer[] renderers;

        /// <summary>Shows the given companion next to the target, or removes the current one when id is null.</summary>
        public static void Show(Transform follow, string id, string variantId)
        {
            Release();
            if (follow == null || string.IsNullOrEmpty(id)) return;

            CompanionFollower f;
            if (Pool.TryGetValue(id, out var stack) && stack.Count > 0) { f = stack.Pop(); f.gameObject.SetActive(true); }
            else f = Build(id);

            f.target = follow;
            f.companionId = id;
            f.transform.position = follow.position + f.offset;
            f.Tint(variantId);
            active = f;
        }

        public static void Release()
        {
            if (active == null) return;
            active.gameObject.SetActive(false);
            if (!Pool.TryGetValue(active.companionId, out var stack)) Pool[active.companionId] = stack = new Stack<CompanionFollower>();
            stack.Push(active);
            active = null;
        }

        private static CompanionFollower Build(string id)
        {
            var go = new GameObject("Companion_" + id);
            var f = go.AddComponent<CompanionFollower>();
            f.phase = Random.value * 6.28f;

            var root = new GameObject("Visual").transform;
            root.SetParent(go.transform, false);
            f.visual = root;
            var body = MaterialFactory.Lit(ColorFor(id), 0.2f, 0.6f);
            var glow = MaterialFactory.Unlit(Color.white);

            PrimitiveType shape = id == "bolt" || id == "pip" ? PrimitiveType.Cube : id == "orbit" || id == "nova" ? PrimitiveType.Sphere : PrimitiveType.Capsule;
            MaterialFactory.Primitive(shape, "Body", root, Vector3.zero, Vector3.one * 0.32f, body);
            MaterialFactory.Primitive(PrimitiveType.Sphere, "Eye", root, new Vector3(0f, 0.04f, 0.15f), Vector3.one * 0.09f, glow);
            MaterialFactory.Primitive(PrimitiveType.Sphere, "Thruster", root, new Vector3(0f, -0.2f, 0f), new Vector3(0.12f, 0.05f, 0.12f), glow);

            // Placeholder primitives ship with colliders; a cosmetic must never have one.
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
            f.renderers = go.GetComponentsInChildren<Renderer>(true);
            return f;
        }

        private static Color ColorFor(string id)
        {
            switch (id)
            {
                case "bolt": return new Color(0.2f, 0.7f, 1f);
                case "pip": return new Color(1f, 0.75f, 0.2f);
                case "orbit": return new Color(0.7f, 0.4f, 1f);
                case "glimmer": return new Color(0.4f, 1f, 0.7f);
                case "cinder": return new Color(1f, 0.4f, 0.25f);
                case "tide": return new Color(0.25f, 0.6f, 0.9f);
                default: return new Color(1f, 0.95f, 0.8f);
            }
        }

        // Variants are cosmetic colour shifts here; real variants will swap materials or meshes.
        private void Tint(string variantId)
        {
            if (renderers == null || renderers.Length == 0 || variantId == "default" || string.IsNullOrEmpty(variantId)) return;
            var c = Color.HSVToRGB(Mathf.Abs(variantId.GetHashCode() % 100) / 100f, 0.6f, 1f);
            MaterialFactory.SetColor(renderers[0].sharedMaterial, c);
        }

        private void LateUpdate()
        {
            if (target == null) return;
            float t = Time.time + phase;
            var wanted = target.position + target.rotation * offset + Vector3.up * (Mathf.Sin(t * 2f) * 0.06f);
            transform.position = Vector3.Lerp(transform.position, wanted, 1f - Mathf.Exp(-6f * Time.deltaTime));
            if (visual != null) visual.localRotation = Quaternion.Euler(0f, t * 40f, Mathf.Sin(t * 1.5f) * 6f);
        }
    }
}
