﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace Sigil.Impl
{
    internal static class ExtensionMethods
    {
        public static bool IsCall(this OpCode op)
        {
            return
                op == OpCodes.Call ||
                op == OpCodes.Calli ||
                op == OpCodes.Callvirt;
        }

        public static void Each<T>(this IEnumerable<T> e, Action<T> a)
        {
            foreach (var x in e)
            {
                a(x);
            }
        }

        public static void Each<T>(this T[] e, Action<T> a)
        {
            for (var i = 0; i < e.Length; i++)
            {
                a(e[i]);
            }
        }

        public static bool Any<T>(this List<T> e, Func<T, bool> p)
        {
            for (var i = 0; i < e.Count; i++)
            {
                if (p(e[i])) return true;
            }

            return false;
        }

        public static bool StartsWithVowel(this IEnumerable<char> str)
        {
            var c = char.ToLower(str.ElementAt(0));

            return "aeiou".Contains(c);
        }

        public static TransitionWrapper Wrap(this IEnumerable<StackTransition> transitions, string method)
        {
            return TransitionWrapper.Get(method, transitions);
        }

        public static string ErrorMessageString(this IEnumerable<TypeOnStack> types)
        {
            var names = types.Select(t => t.ToString()).OrderBy(n => n).ToArray();

            if (names.Length == 1) return names[0];

            var ret = new StringBuilder();
            ret.Append(names[0]);

            for (var i = 1; i < names.Length - 1; i++)
            {
                ret.Append(", " + names[i]);
            }

            ret.Append(", or " + names[names.Length - 1]);

            return ret.ToString();
        }

        public static bool IsVolatile(this FieldInfo field)
        {
            // field builder doesn't implement GetRequiredCustomModifiers
            if (field is FieldBuilder) return false;

            return Array.IndexOf(field.GetRequiredCustomModifiers(), typeof(System.Runtime.CompilerServices.IsVolatile)) >= 0;
        }

        public static bool IsPrefix(this OpCode op)
        {
            return
                op == OpCodes.Tailcall ||
                op == OpCodes.Readonly ||
                op == OpCodes.Volatile ||
                op == OpCodes.Unaligned;
        }

        private static Dictionary<Type, Type> _AliasCache = new Dictionary<Type, Type>
        {
            { typeof(bool), typeof(int) },
            { typeof(sbyte), typeof(int) },
            { typeof(byte), typeof(int) },
            { typeof(short), typeof(int) },
            { typeof(ushort), typeof(int) },
            { typeof(uint), typeof(int) },
            { typeof(ulong), typeof(long) },
            { typeof(IntPtr), typeof(NativeIntType) },
            { typeof(UIntPtr), typeof(NativeIntType) }
        };

        public static Type Alias(this Type t)
        {
            if (t.IsValueType && t.IsEnum)
            {
                return Alias(Enum.GetUnderlyingType(t));
            }

            Type ret;
            if (!_AliasCache.TryGetValue(t, out ret)) ret = t;

            return ret;
        }

        public static bool IsAssignableFrom(this Type type1, TypeOnStack type2)
        {
            return TypeOnStack.Get(type1).IsAssignableFrom(type2);
        }

        public static bool IsAssignableFrom(this TypeOnStack type1, TypeOnStack type2)
        {
            // wildcards match *everything*
            if (type1 == TypeOnStack.Get<WildcardType>() || type2 == TypeOnStack.Get<WildcardType>()) return true;

            if (type1.Type == typeof(AnyPointerType) && type2.IsPointer) return true;
            if (type2.Type == typeof(AnyPointerType) && type1.IsPointer) return true;

            // Native int can be convereted to any pointer type
            if (type1.IsPointer && type2 == TypeOnStack.Get<NativeIntType>()) return true;
            if (type2.IsPointer && type1 == TypeOnStack.Get<NativeIntType>()) return true;

            if ((type1.IsPointer || type1.IsReference) && !(type2.IsPointer || type2.IsReference)) return false;
            if ((type2.IsPointer || type2.IsReference) && !(type1.IsPointer || type1.IsReference)) return false;

            if (type1.IsPointer || type1.IsReference)
            {
                return type1.Type.GetElementType() == type2.Type.GetElementType();
            }

            var t1 = type1.Type;
            var t2 = type2.Type;

            t1 = Alias(t1);
            t2 = Alias(t2);

            // The null type can be assigned to any reference type
            if (t2 == typeof(NullType) && !t1.IsValueType) return true;

            return ReallyIsAssignableFrom(t1, t2);
        }

        private static List<Type> GetBases(Type t)
        {
            if (t.IsValueType) return new List<Type>();

            var ret = new List<Type>();
            t = t.BaseType;

            while (t != null)
            {
                ret.Add(t);
                t = t.BaseType;
            }

            return ret;
        }

        private static bool ReallyIsAssignableFrom(Type t1, Type t2)
        {
            if (t1 == t2) return true;

            if (t1 == typeof(OnlyObjectType))
            {
                if (t2 == typeof(object)) return true;

                return false;
            }

            // quick and dirty base case
            if (t1 == typeof(object) && !t2.IsValueType) return true;

            var t1Bases = GetBases(t1);
            var t2Bases = GetBases(t2);

            if (t2Bases.Any(t2b => TypeOnStack.Get(t1).IsAssignableFrom(TypeOnStack.Get(t2b)))) return true;

            if (t1.IsInterface)
            {
                var t2Interfaces = t2.GetInterfaces();

                return t2Interfaces.Any(t2i => TypeOnStack.Get(t1).IsAssignableFrom(TypeOnStack.Get(t2i)));
            }

            if (t1.IsGenericType && t2.IsGenericType)
            {
                var t1Def = t1.GetGenericTypeDefinition();
                var t2Def = t2.GetGenericTypeDefinition();

                if (t1Def != t2Def) return false;

                var t1Args = t1.GetGenericArguments();
                var t2Args = t2.GetGenericArguments();

                for (var i = 0; i < t1Args.Length; i++)
                {
                    if (!TypeOnStack.Get(t1Args[i]).IsAssignableFrom(TypeOnStack.Get(t2Args[i]))) return false;
                }

                return true;
            }

            try
            {
                return t1.IsAssignableFrom(t2);
            }
            catch (NotSupportedException) 
            { 
                // Builders, some generic types, and so on don't implement this; just assume it's *no good* for now
                return false; 
            }
        }

        private static List<TypeOnStack> _PeekWildcard = new List<TypeOnStack>(new[] { TypeOnStack.Get<WildcardType>() });
        public static List<TypeOnStack>[] Peek(this Stack<List<TypeOnStack>> stack, bool baseless, int n)
        {
            if (stack.Count < n && !baseless) return null;

            var ret = new List<TypeOnStack>[n];

            int i;
            for (i = 0; i < n && i < stack.Count; i++)
            {
                ret[i] = stack.ElementAt(i);
            }

            while (i < n)
            {
                ret[i] = _PeekWildcard;
                i++;
            }

            return ret;
        }
    }
}
