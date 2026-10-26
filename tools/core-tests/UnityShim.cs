// Test-only managed shim of the few UnityEngine APIs that Noxy.Core uses, so Core logic can run under plain .NET.
using System;
using System.Text.Json;

namespace UnityEngine
{
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color gray => new Color(.5f, .5f, .5f, 1f);
        public static Color white => new Color(1f, 1f, 1f, 1f);
        public static Color operator *(Color c, float f) => new Color(c.r * f, c.g * f, c.b * f, c.a);
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }

    public enum KeyCode
    {
        None = 0, Backspace = 8, Tab = 9, Return = 13, Escape = 27, Space = 32,
        Alpha0 = 48, Alpha1, Alpha2, Alpha3, Alpha4, Alpha5, Alpha6, Alpha7, Alpha8, Alpha9,
        A = 97, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        KeypadEnter = 271, LeftShift = 304, LeftControl = 306, Mouse0 = 323, Mouse1 = 324
    }

    public static class Mathf
    {
        public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
        public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        public static float Clamp01(float v) => Clamp(v, 0f, 1f);
        public static int RoundToInt(float v) => (int)Math.Round(v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float Abs(float v) => Math.Abs(v);
        public static float Min(float a, float b) => Math.Min(a, b);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static int Min(int a, int b) => Math.Min(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static float Approximately(float a, float b) => Math.Abs(a - b) < 1e-6f ? 1 : 0;
    }

    public static class JsonUtility
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        public static string ToJson(object o, bool pretty = false) => JsonSerializer.Serialize(o, o.GetType(), Options);
        public static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    }

    public static class Debug
    {
        public static void LogWarning(object o) => Console.WriteLine("WARN " + o);
        public static void Log(object o) => Console.WriteLine(o);
        public static void LogError(object o) => Console.WriteLine("ERR " + o);
    }
}
