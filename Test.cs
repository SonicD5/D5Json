using SonicD5.Json.Packs;
using System.Diagnostics;

namespace SonicD5.Json;

internal static class Test {

	private unsafe static void Main(string[] args) {

		var sw = Stopwatch.StartNew();

		//JsonSerializer.Deserialize<Dictionary<string, object?>>("""
		//{
		//	// g
		//	/* ogo */'fucking_god': 1000210968, //f
		//	'abebe': null,
		//	/*fffff*/
		//	"IDK_BOOL": true,
		//	// fuck?
		//	fuck: "2022-02-24T14:00:00Z",
		//	idk: 'GO FUCK \
		//YOURSELF'}
		//""",
		//new() {
		//	DeserializationPack = [
		//		(ref buf, lt, _, inv) => JsonSystemPack.StringKeyDictionaryDeserialization(ref buf, lt, inv),
		//		(ref buf, lt, _, _) => JsonSystemPack.DateTimeDeserialization(ref buf, lt)
		//	],
		//	DynamicAvalableTypes = {
		//		typeof(DateTime),
		//		typeof(string),
		//		typeof(bool),
		//	}
		//});

		JsonSerializer.TryDeserialize("[1, 2, 3]", out int[]? result);


		sw.Stop();
		Console.WriteLine($"Took {sw.ElapsedMilliseconds} ms or {sw.ElapsedTicks} ticks");
	}
}

