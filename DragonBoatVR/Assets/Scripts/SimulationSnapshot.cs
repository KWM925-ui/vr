using System;
using System.Collections.Concurrent;
using System.Reflection;
using UnityEngine.Scripting;

namespace DragonBoatVR
{
    /// <summary>Copy the serializable, acyclic simulation DTO graph without a
    /// decimal JSON round trip. Scalars keep their original bits; every mutable
    /// object and array is isolated from the producer and other subscribers.</summary>
    [Preserve]
    public static class SimulationSnapshot
    {
        static readonly MethodInfo ShallowCopy = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly ConcurrentDictionary<Type, FieldInfo[]> MutableFields = new ConcurrentDictionary<Type, FieldInfo[]>();

        public static T Copy<T>(T source) => (T)CopyObject(source);

        static bool NeedsCopy(Type type)
        {
            if (type == typeof(string) || type.IsPrimitive || type.IsEnum) return false;
            if (!type.IsValueType) return true;
            return Fields(type).Length > 0;
        }

        static FieldInfo[] Fields(Type type) => MutableFields.GetOrAdd(type, value =>
            Array.FindAll(value.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                field => NeedsCopy(field.FieldType)));

        static object CopyObject(object source)
        {
            if (source == null) return null;
            Type type = source.GetType();
            if (!NeedsCopy(type)) return source;
            if (source is Array array)
            {
                var copy = (Array)array.Clone();
                if (NeedsCopy(type.GetElementType()))
                {
                    if (array.Rank != 1) throw new InvalidOperationException("Snapshot DTO arrays must have one dimension.");
                    for (int i = 0; i < array.Length; i++) copy.SetValue(CopyObject(array.GetValue(i)), i);
                }
                return copy;
            }
            if (!type.IsSerializable || typeof(UnityEngine.Object).IsAssignableFrom(type))
                throw new InvalidOperationException("Snapshot contains a non-DTO object: " + type.FullName);
            object result = ShallowCopy.Invoke(source, null);
            foreach (var field in Fields(type)) field.SetValue(result, CopyObject(field.GetValue(source)));
            return result;
        }
    }
}
