using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace SonicD5.Json;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class JsonSerializableAttribute(string name) : Attribute {
	public string Name { get; init; } = name;
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class JsonSerializationIgnoreAttribute : Attribute { }

public enum NamingConvetions { Any, CamelCase, SnakeCase, PascalCase, KebabCase }
public enum ObjectFieldConventions { NoQuote, SingleQuote = '\'', DoubleQuote = '\"' }

public class JsonException : Exception {
	public JsonException() { }
	public JsonException(string? message) : base(message) { }
	public JsonException(string? message, Exception? innerException) : base(message, innerException) { }
}

public class JsonReflectionException(string? message) : Exception(message) { 
	public JsonReflectionException() : this("Invalid type") { }
}

public class JsonSyntaxException(string? message, JsonReadBuffer? buffer = null) : Exception(buffer == null ? message : $"{message}, throwed at ({buffer.LineIndex}:{buffer.BufferIndex})") { 
	public JsonSyntaxException(JsonReadBuffer? buffer = null) : this("Wrong syntax", buffer) { }
}


public delegate void JsonSerializationInvoker(object? obj, LinkedElement<Type> linkedType, int indentCount);
public delegate bool JsonSerialization(StringBuilder sb, object obj, LinkedElement<Type> linkedType, JsonSerializer.SerializationConfig config, int indentCount, JsonSerializationInvoker invoker);
public delegate object? JsonDeserializationInvoker(JsonReadBuffer buffer, LinkedElement<Type> linkedType);
public delegate object? JsonDeserialization(JsonReadBuffer buffer, LinkedElement<Type> linkedType, JsonSerializer.DeserializationConfig config, JsonDeserializationInvoker invoker);

public class LinkedElement<T> {
	public T Value { get; init; }
	public LinkedElement<T>? Previous { get; init; }
	public LinkedElement<T> Last { get; private set; }

	public LinkedElement(T value, LinkedElement<T>? previous) {
		Value = value;
		Previous = previous;
		Last = this;
		if (previous != null) for (var e = Previous; e != null; e = e.Previous) e.Last = this;
	}

	public Stack<T> ToStack() {
		Stack<T> result = [];
		for (var e = this; e != null; e = e.Previous) result.Push(e.Value);
		return result;
	}

	public override string ToString() {
		StringBuilder sb = new();
		if (Previous != null) {
			if (Previous.Previous != null) sb.Append(".. -> ");
			sb.Append($"{Previous.Value} -> "); 
		}
		sb.Append($"{Value}");
		if (Last != this) {
			if (Last.Previous != this) sb.Append($" -> ..");
			sb.Append($" -> {Last.Value}");
		}
		return sb.ToString();
	}
}

public static partial class JsonSerializer {
	public const string Null = "null";

	public sealed class SerializationConfig {
		private readonly string _indent = "";

		public NamingConvetions NamingConvetion { get; init; } = NamingConvetions.Any;
		public ObjectFieldConventions ObjectFieldConvention { get; init; } = ObjectFieldConventions.DoubleQuote;
		public string Indent {
			get => _indent;
			init {
				for (int i = 0; i < value.Length; i++) if (!char.IsWhiteSpace(value[i])) throw new JsonSyntaxException("Wrong indent syntax");
				_indent = value;
			}
		}
		public bool IgnoreNullValues { get; init; }
		public HashSet<JsonSerialization> SerializationPack { get; init; } = [];
	}

	public sealed class DeserializationConfig {
		public bool RequiredNamingEquality { get; init; }
		public HashSet<JsonDeserialization> DeserializationPack { get; init; } = [];
		public HashSet<Type> DynamicAvalableTypes { get; init; } = [];
	}

	private static MemberInfo[] GetFieldsAndProperties(Type type) => [.. type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
		.Where(m => m.MemberType == MemberTypes.Field || (m is PropertyInfo p && p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0))];


	public static string Serialize(object? obj, SerializationConfig config) {
		if (obj == null) return Null;
		StringBuilder sb = new();
		Serialize(sb, obj, new(obj.GetType(), null), config, 0);
		return sb.ToString();
	}

	public static string Serialize(object? obj) => Serialize(obj, new());

	private static void Serialize(StringBuilder sb, object? obj, LinkedElement<Type> linkedType, SerializationConfig config, int indentCount) {
		if (obj == null) { sb.Append(Null); return; }
		try {
			if (config.SerializationPack.Any(s => s.Invoke(sb, obj, linkedType, config, indentCount, (o, lt, i) => Serialize(sb, o, lt, config, i)))) return;
			if (!DefaultSerialization(sb, obj, linkedType, config, indentCount)) throw new JsonReflectionException();
		} catch (Exception inner) { 
			throw linkedType.Previous == null ? new JsonException($"Serialization has been failed (Type Stack: {string.Join(" -> ", linkedType.Last.ToStack().Select(t => StringType(t, true)))}): ", inner) : inner; 
		}
	}

	public static bool TrySerialize(object? obj, SerializationConfig config, out string result) {
		try {
			result = Serialize(obj, config);
			return true;
		} catch {
			result = "";
			return false;
		}
	}

	public static bool TrySerialize(object? obj, out string result) => TrySerialize(obj, new(), out result);

	private static bool DefaultSerialization(StringBuilder sb, object obj, LinkedElement<Type> linkedType, SerializationConfig config, int indentCount) {
		Type type = linkedType.Value;
		if (type == typeof(string)) { 
			sb.Append($"\"{((string)obj).Replace("\"", "\\\"")}\"");
			return true;
		} 
		if (type.IsPrimitive || type == typeof(decimal)) {
			sb.Append(obj switch {
				float f => f.ToString(CultureInfo.InvariantCulture),
				double d => d.ToString(CultureInfo.InvariantCulture),
				decimal d => d.ToString(CultureInfo.InvariantCulture),
				bool b => b ? "true" : "false",
				_ => obj.ToString()
			});
			return true;
		}
			
		if (type.IsEnum) {
			var field = type.GetFields().First(f => f.Name == Enum.GetName(type, obj));

			sb.Append(field.IsDefined(typeof(JsonSerializableAttribute)) ? field.GetCustomAttribute<JsonSerializableAttribute>()!.Name :
				field.Name.ConvertCase(config.NamingConvetion));
			return true;
		} 
			
		if (type.IsArray) return SerializeArray(sb, (Array)obj, linkedType, config, indentCount);

		Type[] interfaceTypes = type.GetInterfaces();

		if (TryFindInterfaceType(interfaceTypes, i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>),
			out Type enumerableType))
			return SerializeCollection(sb, (IEnumerable)obj, new(enumerableType.GetGenericArguments()[0], linkedType), config, indentCount);
		if (type.IsValueType || type.IsClass) return SerializeObject(sb, obj, linkedType, config, indentCount);
		return false;
	}

	private static bool SerializeObject(StringBuilder sb, object obj, LinkedElement<Type> linkedType, SerializationConfig config, int indentCount) {
		var members = GetFieldsAndProperties(linkedType.Value);
		bool isNotFirst = false;
		bool hasIndent = config.Indent != "";
		int newIndentCount = hasIndent ? indentCount + 1 : 0;
		string quoute = config.ObjectFieldConvention == ObjectFieldConventions.NoQuote ? "" : ((char)config.ObjectFieldConvention).ToString();

		if (indentCount > 0) sb.AppendLine();
		sb.Append(config.Indent.Repeat(indentCount));
		sb.Append('{');

		foreach (var m in members) {
			if (m.IsDefined(typeof(JsonSerializationIgnoreAttribute))) continue;

			var f = m as FieldInfo;
			if (f != null && (f.IsSecurityCritical || (config.IgnoreNullValues && f.GetValue(obj) == null))) continue;

			var p = m as PropertyInfo;
			if (p != null && config.IgnoreNullValues && p.GetValue(obj) == null) continue;

			if (isNotFirst) {
				sb.Append(',');
				if (hasIndent) sb.AppendLine();
			} else {
				if (hasIndent) sb.AppendLine();
				isNotFirst = true; 
			}
			sb.Append($"{config.Indent.Repeat(newIndentCount)}" +
				$"{quoute}{m.GetCustomAttribute<JsonSerializableAttribute>()?.Name ?? m.Name.ConvertCase(config.NamingConvetion)}{quoute}:" +
				$"{(hasIndent ? ' ' : "")}");
			if (f != null) Serialize(sb, f.GetValue(obj), new(f.FieldType, linkedType), config, newIndentCount);
			else if (p != null) Serialize(sb, p.GetValue(obj), new(p.PropertyType, linkedType), config, newIndentCount);

		}
		if (isNotFirst) {
			if (hasIndent) sb.AppendLine();
			sb.Append(config.Indent.Repeat(indentCount));
		}
		sb.Append('}');
		return true;
	}

	private static bool SerializeArray(StringBuilder sb, Array array, LinkedElement<Type> linkedType, SerializationConfig config, int indentCount) {
		if (array.Length == 0) {
			sb.Append("[]");
			return true;
		}
		var eType = linkedType.Value.GetElementType()!;
		bool isObjElement = eType == typeof(object);
		bool isNotFirst = false;
		bool hasIndent = config.Indent != "";
		int newIndentCount = hasIndent ? indentCount + 1 : 0;

		if (indentCount > 0) sb.AppendLine();
		sb.Append(config.Indent.Repeat(indentCount));
		sb.Append('[');

		foreach (var value in array) { 
			if (isNotFirst) {
				sb.Append(',');
				if (hasIndent) sb.AppendLine();
			} else {
				if (hasIndent) sb.AppendLine();
				isNotFirst = true; 
			}
			sb.Append(config.Indent.Repeat(newIndentCount));
			Serialize(sb, value, new(!isObjElement ? eType : (value == null ? typeof(object) : value.GetType()), linkedType), config, newIndentCount);
		}
		if (isNotFirst) {
			if (hasIndent) sb.AppendLine();
			sb.Append(config.Indent.Repeat(indentCount));
		} 
		sb.Append(']');
		return true;
	}

	private static bool SerializeCollection(StringBuilder sb, IEnumerable collection, LinkedElement<Type> linkedEType, SerializationConfig config, int indentCount) {
		bool isObjElement = linkedEType.Value == typeof(object);
		bool isNotFirst = false;
		bool hasIndent = config.Indent != "";
		int newIndentCount = hasIndent ? indentCount + 1 : 0;

		if (indentCount > 0) sb.AppendLine();
		sb.Append(config.Indent.Repeat(indentCount));
		sb.Append('[');
		
		foreach (var item in collection) {
			if (config.IgnoreNullValues && item == null) continue;
			if (isNotFirst) {
				sb.Append(',');
				if (hasIndent) sb.AppendLine();
			} else {
				if (hasIndent) sb.AppendLine();
				isNotFirst = true; 
			}
			sb.Append(config.Indent.Repeat(newIndentCount));
			Serialize(sb, item, new(!isObjElement ? linkedEType.Value : (item == null ? typeof(object) : item.GetType()), linkedEType.Previous), config, newIndentCount);
		}
		if (isNotFirst) {
			if (hasIndent) sb.AppendLine();
			sb.Append(config.Indent.Repeat(indentCount));
		}
		sb.Append(']');
		return true;
	}

	public static object? Deserialize(string json, Type type, DeserializationConfig config) {
		if (string.IsNullOrWhiteSpace(json)) return default;
		return Deserialize(new JsonReadBuffer(json), new(type, null), config);
	}
	public static object? Deserialize(string json, Type type) => Deserialize(json, type, new());
	public static T? Deserialize<T>(string json, DeserializationConfig config) => (T?)Deserialize(json, typeof(T), config);
	public static T? Deserialize<T>(string json) => Deserialize<T>(json, new());

	public static bool TryDeserialize(string json, Type type, DeserializationConfig config, out object? result) {
		try {
			result = Deserialize(json, type, config);
			return true;
		} catch {
			result = default;
			return false;
		}
	}

	public static bool TryDeserialize<T>(string json, DeserializationConfig config, out T? result) {
		try {
			result = Deserialize<T>(json, config);
			return true;
		} catch {
			result = default;
			return false;
		}
	}

	public static bool TryDeserialize(string json, Type type, out object? result) => TryDeserialize(json, type, new(), out result);
	public static bool TryDeserialize<T>(string json, out T? result) => TryDeserialize(json, new(), out result);
	

	private static object? Deserialize(JsonReadBuffer buffer, LinkedElement<Type> linkedType, DeserializationConfig config) {
		Type type = linkedType.Value;
		var nullableValue = Nullable.GetUnderlyingType(type);
		if (buffer.NextIsNull()) {
			if (type.IsClass || nullableValue != null) return null;
			throw new JsonReflectionException("This type isn't nullable");
		}
		if (nullableValue != null) return Deserialize(buffer, new(nullableValue, linkedType.Previous), config);

		try {
			if (type == typeof(object)) {
				foreach (var t in config.DynamicAvalableTypes) {
					try {
						var tempBuf = buffer.Clone();
						var obj = Deserialize(tempBuf, new(t, null), config);
						if (obj == null) continue;
						buffer = tempBuf;
						return obj;
					} catch { continue; }
				}
				throw new JsonReflectionException("Invalid dynamic type cast");
			}

			if (type.IsInterface || type.IsAbstract) throw new JsonReflectionException("The type cannot be ethier an interface or an abstract class");
			object? result = null;
			foreach (var d in config.DeserializationPack) {
				if (result != null) return result;
				result = d.Invoke(buffer, linkedType, config, (buf, lt) => Deserialize(buf, lt, config));
			}
			result ??= DefaultDeserialization(buffer, linkedType, config);
			if (result == null) throw new JsonReflectionException();
			return result;
		} catch (Exception inner) {
			throw linkedType.Previous == null ? new JsonException($"Deserialization has been failed (Type Stack: {string.Join(" -> ", linkedType.Last.ToStack().Select(t => StringType(t, true)))}): ", inner) : inner;
		}
	}

	public static readonly Regex HexRegex = new(@"^[-+]?0[xX]");
	private static T HexApplier<T>(T number, bool isNegative) where T : ISignedNumber<T> => isNegative ? -number : number;

	private static object? DefaultDeserialization(JsonReadBuffer buffer, LinkedElement<Type> linkedType, DeserializationConfig config) {
		Type type = linkedType.Value;
		if (type == typeof(string)) return buffer.ReadString();

		Type[] interfaceTypes = type.GetInterfaces();
		if (type.IsPrimitive || type == typeof(decimal)) {
			string raw = buffer.ReadPrimitive();
			bool isHex = HexRegex.IsMatch(raw);
			bool isNegative = raw.StartsWith('-');
			if (isHex && isNegative && TryFindInterfaceType(interfaceTypes, i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISignedNumber<>), out _)) raw = raw.Replace("-", "");
			return Type.GetTypeCode(type) switch {
				TypeCode.Boolean => bool.Parse(raw),
				TypeCode.Single => float.Parse(raw, CultureInfo.InvariantCulture),
				TypeCode.Double => double.Parse(raw, CultureInfo.InvariantCulture),
				TypeCode.Decimal => decimal.Parse(raw, CultureInfo.InvariantCulture),

				TypeCode.Byte => isHex ? Convert.ToByte(raw, 16) : byte.Parse(raw),
				TypeCode.Int16 => isHex ? HexApplier(Convert.ToInt16(raw, 16), isNegative) : short.Parse(raw),
				TypeCode.Int32 => isHex ? HexApplier(Convert.ToInt32(raw, 16), isNegative) : int.Parse(raw),
				TypeCode.Int64 => isHex ? HexApplier(Convert.ToInt64(raw, 16), isNegative) : long.Parse(raw),
				TypeCode.SByte => isHex ? HexApplier(Convert.ToSByte(raw, 16), isNegative) : sbyte.Parse(raw),
				TypeCode.UInt16 => isHex ? Convert.ToUInt16(raw, 16) : ushort.Parse(raw),
				TypeCode.UInt32 => isHex ? Convert.ToUInt32(raw, 16) : uint.Parse(raw),
				TypeCode.UInt64 => isHex ? Convert.ToUInt64(raw, 16) : ulong.Parse(raw),
				_ => default
			};
		}
		if (type.IsEnum) {
			var fields = type.GetFields();
			string? raw = buffer.ReadString();

			foreach (var field in fields) {
				if ((field.IsDefined(typeof(JsonSerializableAttribute)) && field.GetCustomAttribute<JsonSerializableAttribute>()?.Name == raw) || 
					(config.RequiredNamingEquality ? field.Name == raw : Enum.GetValues<NamingConvetions>().Any(v => field.Name.ConvertCase(v) == raw)))
					return field.GetValue(null);

			}
			throw new JsonReflectionException($"\"{type.Name}\" enum hasn't \"{raw}\" value");
		}
		if (type.IsArray) return DeserializeArray(buffer, linkedType, config);

		if (TryFindInterfaceType(interfaceTypes, i => i.IsGenericType 
		&& i.GetGenericTypeDefinition() == typeof(IEnumerable<>),
		out Type collectionType)) return DeserializeCollection(buffer, new(collectionType.GetGenericArguments()[0], linkedType), config);
		if (type.IsValueType || type.IsClass) return DeserializeObject(buffer, linkedType, config);
		return null;
	}

	private static Array DeserializeArray(JsonReadBuffer buffer, LinkedElement<Type> linkedType, DeserializationConfig config) {
		var next = buffer.Next();

		if (next != JsonReadBuffer.NextType.Array) throw new JsonSyntaxException($"The object start must be '['", buffer);
		var eType = linkedType.Value.GetElementType()!;
		next = buffer.NextBlock();
		if (next == JsonReadBuffer.NextType.EndArray) return Array.CreateInstance(eType, 0);
		if (next == JsonReadBuffer.NextType.Punctuation) throw new JsonSyntaxException($"Punctuation must not be here", buffer);
		List<object?> elems = [];

		while (true) {
			if (next == JsonReadBuffer.NextType.Undefined) {
				elems.Add(Deserialize(buffer, new(eType, linkedType), config));
				next = buffer.NextBlock();
			}
			if (next == JsonReadBuffer.NextType.Punctuation) {
				next = buffer.NextBlock();
				if (next == JsonReadBuffer.NextType.Punctuation) throw new JsonSyntaxException($"Punctuation must not be here", buffer);
				if (next == JsonReadBuffer.NextType.EndArray) break;
				continue;
			}
			if (next == JsonReadBuffer.NextType.EndArray) break;
			throw new JsonSyntaxException(buffer);
		}

		var array = Array.CreateInstance(eType, elems.Count);
		for (int i = 0; i < elems.Count; i++) array.SetValue(elems[i], i);
		return array;
	}

	private static object DeserializeCollection(JsonReadBuffer buffer, LinkedElement<Type> linkedEtype, DeserializationConfig config) {
		var next = buffer.Next();
	
		if (next != JsonReadBuffer.NextType.Array) throw new JsonSyntaxException($"The object start must be '['", buffer);
		Type type = linkedEtype.Previous!.Value;
		var collection = Activator.CreateInstance(type)!;
		next = buffer.NextBlock();
		if (next == JsonReadBuffer.NextType.EndArray) return collection;
		if (next == JsonReadBuffer.NextType.Punctuation) throw new JsonSyntaxException($"Punctuation must not be here", buffer);

		var addMethod = type.GetMethod("Add")!;

		while (true) {
			if (next == JsonReadBuffer.NextType.Undefined) {
				addMethod.Invoke(collection, [Deserialize(buffer, linkedEtype, config)]);
				next = buffer.NextBlock();
			}
			if (next == JsonReadBuffer.NextType.Punctuation) {
				next = buffer.NextBlock();
				if (next == JsonReadBuffer.NextType.Punctuation) throw new JsonSyntaxException($"Punctuation must not be here", buffer);
				if (next == JsonReadBuffer.NextType.EndArray) break;
				continue;
			}
			if (next == JsonReadBuffer.NextType.EndArray) break;
			throw new JsonSyntaxException(buffer);
		}

		return collection;
	}

	private static object DeserializeObject(JsonReadBuffer buffer, LinkedElement<Type> linkedType, DeserializationConfig config) {
		var next = buffer.Next();
		if (next != JsonReadBuffer.NextType.Block) throw new JsonSyntaxException("The object start must be '{'", buffer);
		object obj = Activator.CreateInstance(linkedType.Value)!;
		next = buffer.NextBlock();
		if (next == JsonReadBuffer.NextType.EndBlock) return obj;
		if (next == JsonReadBuffer.NextType.Punctuation) throw new JsonSyntaxException($"Punctuation must not be here", buffer);

		MemberInfo[] members = GetFieldsAndProperties(linkedType.Value);

		while (true) {
			if (next == JsonReadBuffer.NextType.Undefined) {
				string name = buffer.ReadObjectFieldName();
				var m = members.FirstOrDefault(m => m.GetCustomAttribute<JsonSerializableAttribute>()?.Name == name || (config.RequiredNamingEquality ? m.Name == name : Enum.GetValues<NamingConvetions>().Any(v => m.Name.ConvertCase(v) == name)));
				if (m == null) buffer.SkipObjectField();
				else if (m is PropertyInfo p) p.SetValue(obj, Deserialize(buffer, new(p.PropertyType, linkedType), config));
				else if (m is FieldInfo f) f.SetValue(obj, Deserialize(buffer, new(f.FieldType, linkedType), config));
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
		return obj;
	}
}
public sealed class JsonReadBuffer {
	public enum NextType {
		Punctuation,
		Block,
		EndBlock,
		Array,
		EndArray,
		Undefined,
	}

	private Queue<string> _nextLines = [];
	private string buffer = "";

	public JsonReadBuffer(string text) {
		if (string.IsNullOrWhiteSpace(text)) throw new JsonSyntaxException("Invalid text");

		string line = "";
		for (int i = 0; i < text.Length; i++) {
			char c = text[i];
			bool notUnixNewLine = text.Slice(i, 2) == Environment.NewLine;
			if (notUnixNewLine || c == '\n') {
				_nextLines.Enqueue(line);
				if (notUnixNewLine) i++;
				line = "";
				continue;
			}
			line += c;
		}
		_nextLines.Enqueue(line);
		buffer = _nextLines.Dequeue();
	}

	private bool IsNEOB => BufferIndex < buffer.Length;

	public int BufferIndex { get; private set; } 
	public int LineIndex { get; private set; } 

	public override string ToString() => $"({LineIndex}:{BufferIndex}) -> \"{buffer}\", {_nextLines.Count} lines left";

	public JsonReadBuffer Clone() { 
		var clone = (JsonReadBuffer)MemberwiseClone();
		clone._nextLines = new(_nextLines);
		return clone;
	}

	private void ReadLineBuffer() {
		read:
		if (IsNEOB) return;
		if (!_nextLines.TryDequeue(out string? _buffer)) throw new JsonSyntaxException($"Unexpected end of JSON input", this);
		LineIndex++;
		BufferIndex = 0;
		if (string.IsNullOrWhiteSpace(_buffer)) goto read;
		buffer = _buffer;
	}

	public bool NextIsNull() {
		ReadLineBuffer();
		bool comment = false;
		repeat:
		for (; IsNEOB; BufferIndex++) {
			string slice2 = buffer.Slice(BufferIndex, 2);
			if (slice2 == "//") {
				BufferIndex = buffer.Length;
				break;
			}
			char c = buffer[BufferIndex];
			if (comment && slice2 == "*/") {
				comment = false;
				BufferIndex++;
				continue;
			}
			if (!comment && slice2 == "/*") {
				comment = true;
				BufferIndex += 2;
			}
			if (comment || char.IsWhiteSpace(c)) continue;
			if (c == 'n' && buffer.Slice(BufferIndex, JsonSerializer.Null.Length) == JsonSerializer.Null) {
				BufferIndex += JsonSerializer.Null.Length;
				return true;
			} 
			return false;
		}
		ReadLineBuffer();
		goto repeat;
	}

	public NextType Next() {
		ReadLineBuffer();
		bool comment = false;
		repeat:
		for (; IsNEOB; BufferIndex++) {
			string slice2 = buffer.Slice(BufferIndex, 2);
			if (slice2 == "//") {
				BufferIndex = buffer.Length;
				break;
			}
			char c = buffer[BufferIndex];
			if (comment && slice2 == "*/") {
				comment = false;
				BufferIndex++;
				continue;
			}
			if (!comment && slice2 == "/*") {
				comment = true;
				BufferIndex += 2;
			}
			if (comment || char.IsWhiteSpace(c)) continue;
			switch (c) {
				case ',': BufferIndex++; return NextType.Punctuation;
				case '{': BufferIndex++; return NextType.Block;
				case '}': BufferIndex++; return NextType.EndBlock;
				case '[': BufferIndex++; return NextType.Array;
				case ']': BufferIndex++; return NextType.EndArray;
				default: return NextType.Undefined;
			}
		}
		ReadLineBuffer();
		goto repeat;
	}
	public NextType NextBlock() {
		ReadLineBuffer();
		bool comment = false;
		repeat:
		for (; IsNEOB; BufferIndex++) {
			string slice2 = buffer.Slice(BufferIndex, 2);
			if (slice2 == "//") {
				BufferIndex = buffer.Length;
				break;
			}
			char c = buffer[BufferIndex];
			if (comment && slice2 == "*/") {
				comment = false;
				BufferIndex++;
				continue;
			}
			if (!comment && slice2 == "/*") {
				comment = true;
				BufferIndex += 2;
			}

			if (comment || char.IsWhiteSpace(c)) continue;
			if (c == ',') { BufferIndex++; return NextType.Punctuation; }
			if (c == '}') { BufferIndex++; return NextType.EndBlock; }
			if (c == ']') { BufferIndex++; return NextType.EndArray; } 
			return NextType.Undefined;
		}
		ReadLineBuffer();
		goto repeat;
	}

	internal void SkipObjectField() {
		Stack<char> blocks = [];

		do {
			ReadLineBuffer();
			for (; IsNEOB; BufferIndex++) {
				char c = buffer[BufferIndex];

				switch (c) {
					case '\'':
					case '"': {
						BufferIndex++;
						char quote = c;
						bool isEscaped = false;
						read_string:
						for (; IsNEOB; BufferIndex++) {
							c = buffer[BufferIndex];
							if (c == '\\') { isEscaped = !isEscaped; continue; }
							if (isEscaped) { isEscaped = false; continue; }
							if (c == quote)
								goto end_string;
						}
						if (!IsNEOB) { ReadLineBuffer(); goto read_string; }
						end_string:
						if (blocks.Count == 0) { BufferIndex++; return; }
						break;
					}
					case ',':
					if (blocks.Count == 0)
						return;
					break;
					case '{':
					blocks.Push('}');
					break;
					case '[':
					blocks.Push(']');
					break;
					case '}':
					case ']':
					if (blocks.Count == 0)
						return;
					if (c == blocks.Peek()) {
						blocks.Pop();
						if (blocks.Count == 0) { BufferIndex++; return; }
					} else throw new JsonSyntaxException($"Expected '{blocks.Peek()}' found '{c}'", this);
					break;
				}
			}
		} while (blocks.Count > 0);
	}
	public string ReadObjectFieldName() {
		ReadLineBuffer();
		int start = BufferIndex;
		for (; start < buffer.Length; start++)
			if (!char.IsWhiteSpace(buffer[start]))
				break;
		char quote = buffer[start] switch { '"' => '"', '\'' => '\'', _ => '\0' };

		int end;
		if (quote != '\0') {
			end = ++start;
			bool isSlash = false;
			for (; end < buffer.Length; end++) {
				char c = buffer[end];
				if (c == '\\') { isSlash = !isSlash; continue; }
				if (isSlash) { isSlash = false; continue; }
				if (c == quote)
					break;
			}
			BufferIndex = end + 1;
			SeekPropEndChar();
			return buffer[start..end];
		}
		{
			end = start;
			for (; end < buffer.Length; end++) {
				char c = buffer[end];
				if (c == ':') {
					BufferIndex = end + 1;
					goto skipFinder;
				}
				if (char.IsWhiteSpace(c)) break;
				if (!char.IsLetterOrDigit(c) && c is not '_' and not '$' && (end <= start || c is not ('\\' or '\u200C' or '\u200D'))) {
					throw new JsonSyntaxException($"Invalid character '{c}' in parameter name", this);
				}
			}
			BufferIndex = end + 1;
			SeekPropEndChar();
			skipFinder:
			string value = buffer[start..end];
			if (value.Length == 0) throw new JsonSyntaxException($"Size of property name cannot be zero", this);
			return value;
		}
	}
	public string ReadString() {
		read: 
		ReadLineBuffer();
		
		char quote = '"';
		for (; BufferIndex < buffer.Length; BufferIndex++) {
			char c = buffer[BufferIndex];
			if (c is '"' or '\'') {
				quote = c;
				break;
			}
			if (!char.IsWhiteSpace(c)) throw new JsonSyntaxException($"Expected string start in quote", this);
		}

		BufferIndex++;
		if (!IsNEOB) goto read;

		StringBuilder sb = new();

		int end = BufferIndex;
		for (; end < buffer.Length; end++) {
			char c = buffer[end];
			if (c == '\\' && end + 1 == buffer.Length) {
				end = -1;
				BufferIndex = buffer.Length;
				ReadLineBuffer();
				sb.Append('\n');
				continue;
			}
			if (c == quote) break;
			sb.Append(c);
		}
		if (end == buffer.Length) throw new JsonSyntaxException($"Unterminated string", this); 

		BufferIndex = end + 1;
		return UnescapeString(sb.ToString());
	}

	public string ReadPrimitive() {
		ReadLineBuffer();

		int start = BufferIndex;
		for (; start < buffer.Length; start++)
			if (!char.IsWhiteSpace(buffer[start])) break;
		if (start == buffer.Length) throw new JsonSyntaxException($"Unexpected end of primitive value", this);

		int end = start;
		for (; end < buffer.Length; end++) {
			char c = buffer[end];
			if (c is ',' or '}' or ']' || char.IsWhiteSpace(c)) break;
		}

		BufferIndex = end;
		return buffer[start..end];
	}

	public bool TryReadString(out string result) => TryRead(ReadString, out result);
	public bool TryReadPrimitive(out string result) => TryRead(ReadPrimitive, out result);

	private bool TryRead(Func<string> reader, out string result) {
		try {
			result = reader.Invoke();
			return true;
		} catch {
			result = "";
			return false;
		}
	}

	private void SeekPropEndChar() {
		repeat:
		while (IsNEOB) {
			char c = buffer[BufferIndex++];
			if (c == ':') return;
			if (!char.IsWhiteSpace(c)) throw new JsonSyntaxException($"Expected ':', found '{c}'", this);
		}
		ReadLineBuffer();
		goto repeat;
	}

	private static string UnescapeString(string str) {

		StringBuilder sb = new();
		bool isEscaped = false;

		for (int i = 0; i < str.Length; i++) {
			if (isEscaped) {
				switch (str[i]) {
					case '\\':
					sb.Append('\\');
					break;
					case '"':
					sb.Append('"');
					break;
					case 'b':
					sb.Append('\b');
					break;
					case 'f':
					sb.Append('\f');
					break;
					case 'n':
					sb.Append('\n');
					break;
					case 'r':
					sb.Append('\r');
					break;
					case 't':
					sb.Append('\t');
					break;
					case 'u':
					// Обработка Unicode escape последовательностей
					if (i + 4 < str.Length) {
						string hex = str.Substring(i + 1, 4);
						if (int.TryParse(hex, NumberStyles.HexNumber,
							CultureInfo.InvariantCulture, out int code)) {
							sb.Append((char)code);
							i += 4;
						} else throw new JsonSyntaxException($"Invalid Unicode escape sequence: \\u{hex}");
					} else throw new JsonSyntaxException("Incomplete Unicode escape sequence");
					break;
					default:
					throw new JsonSyntaxException($"Invalid escape sequence: \\{str[i]}");
				}
				isEscaped = false;
			} else if (str[i] == '\\') isEscaped = true;
			else sb.Append(str[i]);
		}
		if (isEscaped) throw new JsonSyntaxException("Unterminated escape sequence");
		return sb.ToString();
	}
}