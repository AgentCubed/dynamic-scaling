using System;
using System.Collections.Generic;
using System.Linq;

namespace DynamicScaling
{
    internal static class BossChecklistUtils
    {
        public static Dictionary<string, IDictionary<string, object>> NormalizeBossChecklistReturn(object result)
        {
            if (result == null)
                return null;

            var normalized = new Dictionary<string, IDictionary<string, object>>();

            // Common case: Dictionary<string, Dictionary<string, object>>
            if (result is IDictionary<string, IDictionary<string, object>> typedNested)
            {
                foreach (var kv in typedNested)
                    normalized[kv.Key] = kv.Value;
                return normalized;
            }

            // Generic outer dictionary with object values (ExpandoObject, Dictionary<string, object>, etc.)
            if (result is IDictionary<string, object> outerGeneric)
            {
                foreach (var kv in outerGeneric)
                {
                    if (kv.Value == null)
                        continue;

                    if (kv.Value is IDictionary<string, object> innerGeneric)
                    {
                        normalized[kv.Key] = innerGeneric;
                        continue;
                    }

                    if (kv.Value is System.Collections.IDictionary nonGenericInner)
                    {
                        var dd = new Dictionary<string, object>();
                        foreach (var k in nonGenericInner.Keys)
                        {
                            dd[k.ToString()] = nonGenericInner[k];
                        }
                        normalized[kv.Key] = dd;
                        continue;
                    }

                    // Some objects (EntryInfo) expose a ConvertToDictionary(Version) helper
                    var convM = kv.Value.GetType().GetMethod("ConvertToDictionary", new Type[] { typeof(Version) });
                    if (convM != null)
                    {
                        try
                        {
                            var conv = convM.Invoke(kv.Value, new object[] { new Version(1, 6) });
                            if (conv is IDictionary<string, object> innerConv)
                            {
                                normalized[kv.Key] = innerConv;
                                continue;
                            }
                        }
                        catch { }
                    }
                }

                return normalized;
            }

            // Non-generic IDictionary outer
            if (result is System.Collections.IDictionary nonGenericOuter)
            {
                foreach (var rawKey in nonGenericOuter.Keys)
                {
                    var outerKey = rawKey.ToString();
                    var val = nonGenericOuter[rawKey];

                    if (val is IDictionary<string, object> ig)
                    {
                        normalized[outerKey] = ig;
                        continue;
                    }
                    if (val is System.Collections.IDictionary ndInner)
                    {
                        var dd = new Dictionary<string, object>();
                        foreach (var k in ndInner.Keys) dd[k.ToString()] = ndInner[k];
                        normalized[outerKey] = dd;
                        continue;
                    }

                    var convM = val?.GetType().GetMethod("ConvertToDictionary", new Type[] { typeof(Version) });
                    if (convM != null)
                    {
                        try
                        {
                            var conv = convM.Invoke(val, new object[] { new Version(1, 6) });
                            if (conv is IDictionary<string, object> innerConv)
                            {
                                normalized[outerKey] = innerConv;
                                continue;
                            }
                        }
                        catch { }
                    }
                }
                return normalized;
            }

            return null;
        }
    }
}
