using UnityEngine;

namespace Noxy.Lobby
{
    /// <summary>
    /// Original procedural Noxy lobby environment: staging platform, illuminated gateway, distant outpost,
    /// atmospheric lighting and subtle floating particles.
    /// </summary>
    public sealed class LobbyStage : MonoBehaviour
    {
        private static readonly Color AccentColor = new Color(1f, 0.56f, 0.12f);
        private static readonly Color CyanColor = new Color(0.18f, 0.88f, 1f);

        private Transform ringTicks;
        private Transform ringOuter;
        private Material groundGlow, backGlow, beamMat;
        private Light accentLight;
        private Transform[] shards;
        private float[] shardPhase;

        public static LobbyStage Create()
        {
            var existing = FindObjectOfType<LobbyStage>();
            if (existing != null) return existing;
            var go = new GameObject("LobbyStage");
            return go.AddComponent<LobbyStage>();
        }

        private void Awake()
        {
            BuildLighting();
            BuildPlatform();
            BuildGateway();
            BuildOutpost();
            BuildBeams();
            BuildShards();
            BuildParticles();
        }

        private void BuildLighting()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.14f, 0.17f, 0.25f);
            RenderSettings.fog = false;

            var key = NewLight("KeyLight", LightType.Directional, new Color(1f, 0.93f, 0.82f), 1.15f);
            key.transform.rotation = Quaternion.Euler(38f, 205f, 0f);
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.75f;

            var rim = NewLight("RimLight", LightType.Directional, CyanColor, 1.5f);
            rim.transform.rotation = Quaternion.Euler(22f, 18f, 0f);

            var fill = NewLight("FillLight", LightType.Directional, new Color(0.45f, 0.55f, 1f), 0.35f);
            fill.transform.rotation = Quaternion.Euler(10f, 140f, 0f);

            accentLight = NewLight("AccentGlow", LightType.Point, AccentColor, 1.2f);
            accentLight.transform.position = new Vector3(0f, 0.25f, 0.9f);
            accentLight.range = 4.5f;
        }

        private Light NewLight(string name, LightType type, Color color, float intensity)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var l = go.AddComponent<Light>();
            l.type = type;
            l.color = color;
            l.intensity = intensity;
            l.shadows = LightShadows.None;
            return l;
        }

        private void BuildPlatform()
        {
            var platMat = MaterialFactory.Lit(new Color(0.08f, 0.1f, 0.15f), 0.7f, 0.8f);
            MaterialFactory.Primitive(PrimitiveType.Cylinder, "Platform", transform, new Vector3(0f, -0.07f, 0f), new Vector3(3.2f, 0.07f, 3.2f), platMat);

            var edgeMat = MaterialFactory.Unlit(CyanColor);
            MaterialFactory.Primitive(PrimitiveType.Cylinder, "PlatformEdge", transform, new Vector3(0f, 0f, 0f), new Vector3(3.3f, 0.01f, 3.3f), edgeMat);

            ringOuter = new GameObject("RingOuter").transform;
            ringOuter.SetParent(transform, false);
            ringOuter.localPosition = new Vector3(0f, 0.008f, 0f);
            var lineGo = new GameObject("RingLine");
            lineGo.transform.SetParent(ringOuter, false);
            lineGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // lay the ring flat on the platform
            var line = lineGo.AddComponent<LineRenderer>();
            line.loop = true;
            line.useWorldSpace = false;
            line.widthMultiplier = 0.035f;
            line.positionCount = 72;
            line.alignment = LineAlignment.TransformZ;
            line.sharedMaterial = MaterialFactory.Unlit(new Color(1f, 0.56f, 0.12f, 1f));
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            for (int i = 0; i < 72; i++)
            {
                float a = i / 72f * Mathf.PI * 2f;
                line.SetPosition(i, new Vector3(Mathf.Cos(a) * 1.25f, Mathf.Sin(a) * 1.25f, 0f));
            }
            ringTicks = new GameObject("RingTicks").transform;
            ringTicks.SetParent(transform, false);
            var tickMat = MaterialFactory.Unlit(CyanColor);
            const int ticks = 36;
            for (int i = 0; i < ticks; i++)
            {
                float a = i / (float)ticks * Mathf.PI * 2f;
                var tick = MaterialFactory.Primitive(PrimitiveType.Cube, "Tick", ringTicks,
                    new Vector3(Mathf.Cos(a) * 1.5f, 0.006f, Mathf.Sin(a) * 1.5f),
                    new Vector3(i % 3 == 0 ? 0.16f : 0.08f, 0.01f, 0.025f), tickMat);
                tick.transform.localRotation = Quaternion.Euler(0f, -a * Mathf.Rad2Deg, 0f);
                tick.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            groundGlow = MaterialFactory.Glow(ProceduralTextures.RadialGlow, new Color(1f, 0.5f, 0.1f, 0.55f));
            var glow = MaterialFactory.Primitive(PrimitiveType.Quad, "GroundGlow", transform, new Vector3(0f, 0.01f, 0f), new Vector3(5.5f, 5.5f, 1f), groundGlow);
            glow.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            NoShadow(glow);
        }

        private void BuildBeams()
        {
            beamMat = MaterialFactory.Glow(ProceduralTextures.Beam, new Color(0.3f, 0.85f, 1f, 0.22f));
            for (int i = 0; i < 2; i++)
            {
                float side = i == 0 ? -1f : 1f;
                var beam = MaterialFactory.Primitive(PrimitiveType.Quad, "Beam", transform, new Vector3(side * 2.4f, 4.2f, -4f), new Vector3(1.8f, 10f, 1f), beamMat);
                beam.transform.rotation = Quaternion.Euler(0f, 180f, side * 14f);
                NoShadow(beam);
            }

            private void BuildGateway()
            {
                var frame = MaterialFactory.Lit(new Color(0.035f, 0.075f, 0.105f), 0.72f, 0.78f);
                var trim = MaterialFactory.Unlit(CyanColor);
                var warm = MaterialFactory.Unlit(AccentColor);

                for (int side = -1; side <= 1; side += 2)
                {
                    var pillar = MaterialFactory.Primitive(PrimitiveType.Cube, "GatePillar", transform,
                        new Vector3(side * 2.65f, 2.45f, -6.2f), new Vector3(0.42f, 4.9f, 0.48f), frame);
                    NoShadow(pillar);
                    var strip = MaterialFactory.Primitive(PrimitiveType.Cube, "GateLight", transform,
                        new Vector3(side * 2.65f, 2.45f, -5.94f), new Vector3(0.055f, 4.35f, 0.025f), trim);
                    NoShadow(strip);
                    var footing = MaterialFactory.Primitive(PrimitiveType.Cube, "GateFoot", transform,
                        new Vector3(side * 2.65f, 0.22f, -5.9f), new Vector3(0.82f, 0.42f, 0.8f), frame);
                    NoShadow(footing);
                }

                var lintel = MaterialFactory.Primitive(PrimitiveType.Cube, "GateLintel", transform,
                    new Vector3(0f, 4.88f, -6.2f), new Vector3(5.7f, 0.38f, 0.48f), frame);
                NoShadow(lintel);
                var band = MaterialFactory.Primitive(PrimitiveType.Cube, "GateSignalBand", transform,
                    new Vector3(0f, 4.64f, -5.94f), new Vector3(3.2f, 0.045f, 0.025f), warm);
                NoShadow(band);

                var sign = MaterialFactory.Primitive(PrimitiveType.Cube, "NoxyGateMark", transform,
                    new Vector3(0f, 4.9f, -5.91f), new Vector3(0.26f, 0.26f, 0.035f), trim);
                sign.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
                NoShadow(sign);

                var innerFrame = MaterialFactory.Primitive(PrimitiveType.Cube, "GateInnerHeader", transform,
                    new Vector3(0f, 3.35f, -6.08f), new Vector3(1.8f, 0.055f, 0.07f), trim);
                NoShadow(innerFrame);

                for (int side = -1; side <= 1; side += 2)
                {
                    var rail = MaterialFactory.Primitive(PrimitiveType.Cube, "PlatformRail", transform,
                        new Vector3(side * 2.05f, 0.12f, -3.8f), new Vector3(0.06f, 0.12f, 3.2f), trim);
                    NoShadow(rail);
                }

                for (int i = 0; i < 5; i++)
                {
                    var marker = MaterialFactory.Primitive(PrimitiveType.Cube, "ApproachMarker", transform,
                        new Vector3(0f, 0.012f, -0.8f - i * 0.62f),
                        new Vector3(0.34f - i * 0.035f, 0.012f, 0.035f), i % 2 == 0 ? warm : trim);
                    NoShadow(marker);
                }
            }

            private void BuildOutpost()
            {
                var shell = MaterialFactory.Lit(new Color(0.035f, 0.065f, 0.105f), 0.45f, 0.7f);
                var trim = MaterialFactory.Lit(new Color(0.09f, 0.16f, 0.2f), 0.55f, 0.6f);
                var window = MaterialFactory.Unlit(new Color(0.14f, 0.62f, 0.68f));
                var amber = MaterialFactory.Unlit(new Color(1f, 0.48f, 0.12f));
                var rng = new System.Random(9041);

                for (int side = -1; side <= 1; side += 2)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float x = side * (5.4f + i * 2.1f);
                        float height = 3.4f + (float)rng.NextDouble() * (i == 0 ? 2.7f : 3.8f);
                        float z = -9.5f - i * 2.1f;
                        var building = MaterialFactory.Primitive(PrimitiveType.Cube, "OutpostBlock", transform,
                            new Vector3(x, height * 0.5f - 0.2f, z), new Vector3(2.5f, height, 2.1f), i == 0 ? trim : shell);
                        NoShadow(building);

                        var roof = MaterialFactory.Primitive(PrimitiveType.Cube, "OutpostRoof", transform,
                            new Vector3(x, height + 0.02f, z), new Vector3(2.7f, 0.14f, 2.3f), trim);
                        NoShadow(roof);

                        for (int row = 0; row < 3; row++)
                        {
                            for (int col = -1; col <= 1; col++)
                            {
                                var lit = MaterialFactory.Primitive(PrimitiveType.Cube, "OutpostWindow", transform,
                                    new Vector3(x + col * 0.58f, 0.7f + row * 0.72f, z + 1.06f),
                                    new Vector3(0.27f, 0.34f, 0.035f), (row + col + i) % 4 == 0 ? amber : window);
                                NoShadow(lit);
                            }
                        }

                        if (i == 0)
                        {
                            var antenna = MaterialFactory.Primitive(PrimitiveType.Cube, "OutpostAntenna", transform,
                                new Vector3(x, height + 0.62f, z), new Vector3(0.045f, 1.1f, 0.045f), trim);
                            NoShadow(antenna);
                            var beacon = MaterialFactory.Primitive(PrimitiveType.Sphere, "OutpostBeacon", transform,
                                new Vector3(x, height + 1.18f, z), Vector3.one * 0.16f, amber);
                            NoShadow(beacon);
                        }
                    }
                }
            }
        }

        private void BuildShards()
        {
            var mat = MaterialFactory.Lit(new Color(0.07f, 0.1f, 0.16f), 0.6f, 0.8f);
            var edge = MaterialFactory.Unlit(new Color(1f, 0.56f, 0.12f));
            const int count = 9;
            shards = new Transform[count];
            shardPhase = new float[count];
            var rng = new System.Random(21);
            for (int i = 0; i < count; i++)
            {
                float side = i % 2 == 0 ? -1f : 1f;
                var pos = new Vector3(side * (3.5f + (float)rng.NextDouble() * 5f), 0.8f + (float)rng.NextDouble() * 4.5f, -3f - (float)rng.NextDouble() * 6f);
                float s = 0.25f + (float)rng.NextDouble() * 0.5f;
                var shard = MaterialFactory.Primitive(PrimitiveType.Cube, "Shard", transform, pos, new Vector3(s, s * 1.8f, s), mat);
                if (i % 3 == 0)
                {
                    var core = MaterialFactory.Primitive(PrimitiveType.Cube, "Core", shard.transform, Vector3.zero, Vector3.one * 0.35f, edge);
                    NoShadow(core);
                }
                NoShadow(shard);
                shards[i] = shard.transform;
                shardPhase[i] = (float)rng.NextDouble() * 10f;
            }
        }

        private void BuildParticles()
        {
            var go = new GameObject("Dust");
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(0f, 1.8f, -1.5f);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(6f, 10f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.12f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.1f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.4f, 0.9f, 1f, 0.7f), new Color(1f, 0.7f, 0.35f, 0.7f));
            main.maxParticles = 140;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0f;

            var emission = ps.emission;
            emission.rateOverTime = 16f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(9f, 4f, 5f);

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-0.05f, 0.05f);
            vel.y = new ParticleSystem.MinMaxCurve(0.08f, 0.22f);
            vel.z = new ParticleSystem.MinMaxCurve(-0.05f, 0.05f);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(1f, 0.75f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(gradient);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = MaterialFactory.Glow(ProceduralTextures.RadialGlow, Color.white, true);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            ps.Simulate(6f, true, true); // pre-warm so the air is already full of dust on the first frame
            ps.Play();
        }

        private static void NoShadow(GameObject go)
        {
            var r = go.GetComponent<Renderer>();
            if (r != null)
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
        }

        private void Update()
        {
            float t = Time.unscaledTime;
            if (ringTicks != null) ringTicks.localRotation = Quaternion.Euler(0f, t * 12f, 0f);

            float pulse = 0.5f + 0.5f * Mathf.Sin(t * 1.3f);
            if (groundGlow != null) MaterialFactory.SetColor(groundGlow, new Color(1f, 0.5f, 0.1f, Mathf.Lerp(0.4f, 0.7f, pulse)));
            if (backGlow != null) MaterialFactory.SetColor(backGlow, new Color(0.15f, 0.55f, 0.8f, Mathf.Lerp(0.38f, 0.55f, 1f - pulse)));
            if (beamMat != null) MaterialFactory.SetColor(beamMat, new Color(0.3f, 0.85f, 1f, Mathf.Lerp(0.14f, 0.26f, pulse)));
            if (accentLight != null) accentLight.intensity = Mathf.Lerp(0.9f, 1.4f, pulse);

            for (int i = 0; i < shards.Length; i++)
            {
                var s = shards[i];
                float p = shardPhase[i];
                var pos = s.position;
                pos.y += Mathf.Sin(t * 0.6f + p) * 0.12f * Time.unscaledDeltaTime;
                s.position = pos;
                s.Rotate(new Vector3(6f, 14f, 3f) * Time.unscaledDeltaTime, Space.Self);
            }
        }
    }
}
