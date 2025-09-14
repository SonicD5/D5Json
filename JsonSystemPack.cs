using System.Collections;
using System.Globalization;
using System.Text;
using static SonicD5.Json.JsonSerializer;

namespace SonicD5.Json;

public static class JsonSystemPack {

	public static bool CharSerialization(StringBuilder sb, object obj, LinkedElement<Type> linkedType, SerializationConfig config) {
		var type = linkedType.Value;
		if (type != typeof(char) && !char.TryParse(obj.ToString().Escape(config.UnicodeEscape), out _)) return false;
		sb.Append($"\"{obj}\"");
		return true;
	}

	public static bool DateTimeSeriazation(StringBuilder sb, object obj, LinkedElement<Type> linkedType, SerializationConfig config, string? format) {
		if (linkedType.Value != typeof(DateTime)) return false;
		sb.Append(format == null ? ((DateTime)obj - DateTime.UnixEpoch).TotalSeconds : ((DateTime)obj).ToString(format).Escape(config.UnicodeEscape));
		return true;
	}

	public static bool TypeSerialization(StringBuilder sb, object obj, LinkedElement<Type> linkedType) {
		if (!linkedType.Value.IsAssignableTo(typeof(Type))) return false;
		sb.Append($"\"{StringType((Type)obj)}\"");
		return true;
	}

	public static bool StringKeyDictionarySerialization(StringBuilder sb, object obj, LinkedElement<Type> linkedType, SerializationConfig config, int indentCount, JsonSerializationInvoker invoker) {
		if (!TryFindInterfaceType(linkedType.Value.GetInterfaces(), i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>) 
		&& i.GetGenericArguments()[0] == typeof(string), out var strDictType)) return false;
		var dict = (IDictionary)obj;
		if (dict.Count == 0) {
			sb.Append("{}");
			return true;
		}

		LinkedElement<Type> linkedVType = new(strDictType.GetGenericArguments()[1], linkedType);
		bool isObjValue = linkedVType.Value == typeof(object);
		bool isNotFirst = false;
		bool hasIndent = config.Indent != "";
		int newIndentCount = hasIndent ? indentCount + 1 : 0;

		if (indentCount > 0) sb.AppendLine();
		sb.Append(config.Indent.Repeat(indentCount));
		sb.Append('{');

		foreach (string key in dict.Keys) {
			var value = dict[key];
			if (config.IgnoreNullValues && value == null) continue;
			if (isNotFirst) {
				sb.Append(',');
				if (hasIndent)
					sb.AppendLine();
			} else {
				if (hasIndent) sb.AppendLine();
				isNotFirst = true;
			}
			sb.Append(config.Indent.Repeat(newIndentCount));
			invoker.Invoke(key, new(typeof(string), linkedVType.Previous), newIndentCount);
			sb.Append(':');
			if (hasIndent) sb.Append(' ');
			invoker.Invoke(value, new(!isObjValue ? linkedVType.Value : (value == null ? typeof(object) : value.GetType()), linkedVType.Previous), newIndentCount);
		}
		if (isNotFirst) {
			if (hasIndent)
				sb.AppendLine();
			sb.Append(config.Indent.Repeat(indentCount));
		}
		sb.Append('}');
		return true;
	}

	public static object? CharDeserialization(JsonReadBuffer buffer, LinkedElement<Type> linkedType) {
		if (linkedType.Value != typeof(char)) return null;
		if (!char.TryParse(buffer.ReadString(), out char result)) throw new JsonReflectionException();
		return result;
	}

	/*public static object? TypeDeserialization(JsonReadBuffer buffer, LinkedElement<Type> linkedType) {
		AppDomain.
	}*/

	public static object? DateTimeDeserialization(JsonReadBuffer buffer, LinkedElement<Type> linkedType) {
		if (linkedType.Value != typeof(DateTime)) return null;
		var tempBuf = buffer.Clone();

		if (tempBuf.TryReadPrimitive(out string rawUnix) && TryDeserialize(rawUnix, out long unix)) {
			buffer = tempBuf;
			return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
		}
		tempBuf = buffer.Clone();
		if (tempBuf.TryReadString(out string iso) && DateTime.TryParse(iso, CultureInfo.InvariantCulture, out var result)) {
			buffer = tempBuf;
			return result;
		}

		throw new JsonReflectionException();
	}



	public static object? StringKeyDictionaryDeserialization(JsonReadBuffer buffer, LinkedElement<Type> linkedType, JsonDeserializationInvoker invoker) {
		var type = linkedType.Value;
		if (!TryFindInterfaceType(type.GetInterfaces(), i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>) &&
		i.GetGenericArguments()[0] == typeof(string),
		out Type strDictType)) return null;

		var next = buffer.Next();
		if (next != JsonReadBuffer.NextType.Block) throw new JsonSyntaxException();
		var dict = (IDictionary)Activator.CreateInstance(type)!;
		next = buffer.NextBlock();
		if (next == JsonReadBuffer.NextType.EndBlock) return dict;
		if (next != JsonReadBuffer.NextType.Undefined) throw new JsonSyntaxException();

		LinkedElement<Type> linkedVType = new(strDictType.GetGenericArguments()[1], linkedType);

		while (true) {
			if (next == JsonReadBuffer.NextType.Undefined) {
				dict.Add(buffer.ReadObjectFieldName()!, invoker.Invoke(buffer, linkedVType));
				next = buffer.NextBlock();
			}
			if (next == JsonReadBuffer.NextType.Punctuation) {
				next = buffer.NextBlock();
				if (next == JsonReadBuffer.NextType.Punctuation) throw new JsonSyntaxException($"Punctuation must not be here", buffer);
				if (next == JsonReadBuffer.NextType.EndBlock) break;
				continue;
			}
			if (next == JsonReadBuffer.NextType.EndBlock) break;
			throw new JsonSyntaxException(buffer);
		}

		return dict;
	}
}
