// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace LoMScenarioAddon
{
    /// <summary>
    /// scenario.json の値 (MiniJson: double / bool / string / List / Dictionary) をフィールドの型に合わせて書き込む。
    /// 数値・真偽・文字列・列挙 (数値か名前)・配列/List・入れ子の Serializable クラスに対応。Unity オブジェクトへの参照は書けない。
    /// </summary>
    internal static class FieldSetter
    {
        internal static void Apply(object target, Dictionary<string, object> values, string where, List<string> errors)
        {
            foreach (var pair in values)
            {
                var field = AccessTools.Field(target.GetType(), pair.Key);
                if (field == null)
                {
                    errors.Add($"{where}: {target.GetType().Name} has no field '{pair.Key}'");
                    continue;
                }
                try
                {
                    field.SetValue(target, Convert(pair.Value, field.FieldType, field.GetValue(target), $"{where}.{pair.Key}", errors));
                }
                catch (Exception ex)
                {
                    errors.Add($"{where}.{pair.Key}: {ex.Message}");
                }
            }
        }

        private static object Convert(object value, Type type, object current, string where, List<string> errors)
        {
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                throw new NotSupportedException("references to game assets cannot be set");
            if (type.IsEnum)
                return value is string name ? Enum.Parse(type, name) : Enum.ToObject(type, System.Convert.ToInt32(value));
            if (type == typeof(string))
                return value as string ?? value?.ToString();
            if (type == typeof(bool))
                return value is bool b ? b : System.Convert.ToDouble(value) != 0;
            if (type.IsPrimitive)
                return System.Convert.ChangeType(value, type);
            if (value is List<object> list)
            {
                if (type.IsArray)
                {
                    var element = type.GetElementType();
                    var array = Array.CreateInstance(element, list.Count);
                    for (int i = 0; i < list.Count; i++)
                        array.SetValue(Convert(list[i], element, null, $"{where}[{i}]", errors), i);
                    return array;
                }
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var element = type.GetGenericArguments()[0];
                    var result = (IList)Activator.CreateInstance(type);
                    for (int i = 0; i < list.Count; i++)
                        result.Add(Convert(list[i], element, null, $"{where}[{i}]", errors));
                    return result;
                }
            }
            if (value is Dictionary<string, object> nested)
            {
                // 入れ子の設定 (例: サイコロ {_base, _dice, _sides}) は、今の値に部分的に上書きする
                object obj = current ?? Activator.CreateInstance(type);
                Apply(obj, nested, where, errors);
                return obj;
            }
            throw new NotSupportedException($"cannot convert {value?.GetType().Name ?? "null"} to {type.Name}");
        }
    }
}
