using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>Small helpers for reading loosely typed JSON argument dictionaries.</summary>
    public static class J
    {
        public static Dictionary<string, object> AsDict(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static List<object> AsList(object value)
        {
            return value as List<object>;
        }

        public static bool Has(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v);
        }

        public static bool HasValue(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null;
        }

        public static string Str(Dictionary<string, object> d, string key, string def = null)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            var s = v as string;
            if (s != null) return s;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double Num(Dictionary<string, object> d, string key, double def = 0)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            return ToDouble(v, def);
        }

        public static float Float(Dictionary<string, object> d, string key, float def = 0)
        {
            return (float)Num(d, key, def);
        }

        public static int Int(Dictionary<string, object> d, string key, int def = 0)
        {
            return (int)Math.Round(Num(d, key, def));
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool def = false)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(s)) return def;
            s = s.Trim().ToLowerInvariant();
            return s == "1" || s == "true" || s == "yes" || s == "on";
        }

        public static Dictionary<string, object> Dict(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return AsDict(v);
        }

        public static List<object> List(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return AsList(v);
        }

        public static string[] Strings(Dictionary<string, object> d, string key)
        {
            var list = List(d, key);
            if (list == null)
            {
                string single = Str(d, key);
                return single == null ? null : new[] { single };
            }
            var result = new string[list.Count];
            for (int i = 0; i < list.Count; i++) result[i] = Convert.ToString(list[i], CultureInfo.InvariantCulture);
            return result;
        }

        public static double ToDouble(object value, double def = 0)
        {
            if (value == null) return def;
            if (value is double) return (double)value;
            if (value is float) return (float)value;
            if (value is bool) return (bool)value ? 1 : 0;
            if (value is int || value is long || value is short || value is byte) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            string s = value as string;
            if (s != null)
            {
                double r;
                if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
                return def;
            }
            double d2;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d2) ? d2 : def;
        }

        // ------------------------------------------------------------- vectors

        public static Vector3 Vec3(Dictionary<string, object> d, string key, Vector3 def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            return ToVector3(v, def);
        }

        public static Vector3 ToVector3(object value, Vector3 def)
        {
            var list = value as List<object>;
            if (list != null)
            {
                return new Vector3(
                    (float)ToDouble(list.Count > 0 ? list[0] : null, def.x),
                    (float)ToDouble(list.Count > 1 ? list[1] : null, def.y),
                    (float)ToDouble(list.Count > 2 ? list[2] : null, def.z));
            }

            var dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                return new Vector3(
                    (float)Num(dict, "x", def.x),
                    (float)Num(dict, "y", def.y),
                    (float)Num(dict, "z", def.z));
            }

            var s = value as string;
            if (s != null)
            {
                string[] parts = s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1)
                {
                    if (parts.Length >= 3)
                        return new Vector3(ParseF(parts[0], def.x), ParseF(parts[1], def.y), ParseF(parts[2], def.z));
                    float all = ParseF(parts[0], def.x);
                    return new Vector3(all, all, all);
                }
                return def;
            }

            float uniform = (float)ToDouble(value, def.x);
            return new Vector3(uniform, uniform, uniform);
        }

        public static Vector2 Vec2(Dictionary<string, object> d, string key, Vector2 def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;

            var list = v as List<object>;
            if (list != null)
            {
                return new Vector2(
                    (float)ToDouble(list.Count > 0 ? list[0] : null, def.x),
                    (float)ToDouble(list.Count > 1 ? list[1] : null, def.y));
            }
            var dict = v as Dictionary<string, object>;
            if (dict != null)
            {
                return new Vector2((float)Num(dict, "x", def.x), (float)Num(dict, "y", def.y));
            }
            float uniform = (float)ToDouble(v, def.x);
            return new Vector2(uniform, uniform);
        }

        public static Quaternion Quat(Dictionary<string, object> d, string key, Quaternion def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;

            var list = v as List<object>;
            if (list != null && list.Count >= 3)
            {
                return Quaternion.Euler(
                    (float)ToDouble(list[0], 0),
                    (float)ToDouble(list[1], 0),
                    (float)ToDouble(list[2], 0));
            }
            var dict = v as Dictionary<string, object>;
            if (dict != null)
            {
                if (dict.ContainsKey("x") && dict.ContainsKey("y") && dict.ContainsKey("z") && dict.ContainsKey("w"))
                {
                    return new Quaternion(
                        (float)Num(dict, "x", 0),
                        (float)Num(dict, "y", 0),
                        (float)Num(dict, "z", 0),
                        (float)Num(dict, "w", 1));
                }
                return Quaternion.Euler((float)Num(dict, "x", 0), (float)Num(dict, "y", 0), (float)Num(dict, "z", 0));
            }
            var s = v as string;
            if (s != null)
            {
                string[] parts = s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    return Quaternion.Euler(ParseF(parts[0], 0), ParseF(parts[1], 0), ParseF(parts[2], 0));
            }
            float uniform = (float)ToDouble(v, 0);
            return Quaternion.Euler(uniform, uniform, uniform);
        }

        public static Color Col(Dictionary<string, object> d, string key, Color def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            return ToColor(v, def);
        }

        public static Color ToColor(object value, Color def)
        {
            var s = value as string;
            if (s != null)
            {
                Color parsed;
                if (ColorUtility.TryParseHtmlString(s, out parsed)) return parsed;
                return def;
            }

            var list = value as List<object>;
            if (list != null)
            {
                return new Color(
                    (float)ToDouble(list.Count > 0 ? list[0] : null, def.r),
                    (float)ToDouble(list.Count > 1 ? list[1] : null, def.g),
                    (float)ToDouble(list.Count > 2 ? list[2] : null, def.b),
                    (float)ToDouble(list.Count > 3 ? list[3] : null, 1));
            }

            var dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                return new Color(
                    (float)Num(dict, "r", def.r),
                    (float)Num(dict, "g", def.g),
                    (float)Num(dict, "b", def.b),
                    (float)Num(dict, "a", 1));
            }

            float uniform = (float)ToDouble(value, def.r);
            return new Color(uniform, uniform, uniform, 1);
        }

        static float ParseF(string text, float def)
        {
            float r;
            if (float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            return def;
        }
    }
}
