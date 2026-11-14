// Checks that every type/member reference a mod dll makes into the game's sts2 assembly
// resolves (signature-exact) against a given sts2.dll. A source-compatible change such as an
// added optional parameter compiles fine on both game branches but binds the dll to only one
// shape, failing at runtime with MissingMethodException; a plain compile can't catch that.
//
// Usage: dotnet run --project tools/BinaryCompat -- <mod.dll> <sts2.dll>
//        dotnet run --project tools/BinaryCompat -- --list <mod.dll>
// Exit code 1 if any reference is unresolved. --list prints every sts2 reference instead (diff two
// builds' lists to see which references are new when the other branch's sts2.dll isn't at hand).
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

bool listOnly = args.Length == 2 && args[0] == "--list";
if (args.Length != 2) { Console.Error.WriteLine("usage: <mod.dll> <sts2.dll> | --list <mod.dll>"); return 2; }

using var modPe = new PEReader(File.OpenRead(args[listOnly ? 1 : 0]));
using var gamePe = new PEReader(File.OpenRead(args[1])); // unused (the mod itself) under --list
var mod = modPe.GetMetadataReader();
var game = gamePe.GetMetadataReader();
const string GameAsm = "sts2";

// --- index the game assembly -------------------------------------------------------------
var gameProvider = new SigProvider(game);
var types = new Dictionary<string, TypeDefinitionHandle>();
foreach (var h in game.TypeDefinitions) types[gameProvider.DefName(h)] = h;

string? BaseOf(TypeDefinitionHandle h)
{
    var b = game.GetTypeDefinition(h).BaseType;
    if (b.IsNil) return null;
    return b.Kind switch
    {
        HandleKind.TypeDefinition => gameProvider.DefName((TypeDefinitionHandle)b),
        HandleKind.TypeSpecification => gameProvider.GenericDefName(game.GetTypeSpecification((TypeSpecificationHandle)b).Signature),
        _ => null, // base type outside sts2 (Godot, BCL): stop walking
    };
}

bool HasMember(string typeName, string name, string sig, bool isField)
{
    for (string? t = typeName; t != null && types.TryGetValue(t, out var h); t = BaseOf(h))
    {
        var td = game.GetTypeDefinition(h);
        if (isField)
        {
            foreach (var fh in td.GetFields())
            {
                var f = game.GetFieldDefinition(fh);
                if (game.GetString(f.Name) == name && f.DecodeSignature(gameProvider, null) == sig) return true;
            }
        }
        else
        {
            foreach (var mh in td.GetMethods())
            {
                var m = game.GetMethodDefinition(mh);
                if (game.GetString(m.Name) == name && MethodSig(m.DecodeSignature(gameProvider, null)) == sig) return true;
            }
        }
    }
    return false;
}

static string MethodSig(MethodSignature<string> s) =>
    $"{s.ReturnType} ({string.Join(", ", s.ParameterTypes)}){(s.GenericParameterCount > 0 ? $"`{s.GenericParameterCount}" : "")}";

// --- walk the mod's references ------------------------------------------------------------
var modProvider = new SigProvider(mod);
var missing = new SortedSet<string>();
int checkedCount = 0;

bool IsGameRef(TypeReferenceHandle h)
{
    var tr = mod.GetTypeReference(h);
    var scope = tr.ResolutionScope;
    if (scope.Kind == HandleKind.TypeReference) return IsGameRef((TypeReferenceHandle)scope);
    return scope.Kind == HandleKind.AssemblyReference
        && mod.GetString(mod.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) == GameAsm;
}

foreach (var h in mod.TypeReferences)
{
    if (!IsGameRef(h)) continue;
    checkedCount++;
    var name = modProvider.RefName(h);
    if (listOnly) { Console.WriteLine($"type   {name}"); continue; }
    if (!types.ContainsKey(name)) missing.Add($"type   {name}");
}

foreach (var h in mod.MemberReferences)
{
    var mr = mod.GetMemberReference(h);
    string? parent = mr.Parent.Kind switch
    {
        HandleKind.TypeReference when IsGameRef((TypeReferenceHandle)mr.Parent) => modProvider.RefName((TypeReferenceHandle)mr.Parent),
        HandleKind.TypeSpecification => modProvider.GameGenericDefName(mod.GetTypeSpecification((TypeSpecificationHandle)mr.Parent).Signature, IsGameRef),
        _ => null,
    };
    if (parent == null) continue;
    checkedCount++;
    var name = mod.GetString(mr.Name);
    bool isField = mr.GetKind() == MemberReferenceKind.Field;
    string sig = isField ? mr.DecodeFieldSignature(modProvider, null) : MethodSig(mr.DecodeMethodSignature(modProvider, null));
    if (listOnly) { Console.WriteLine($"{(isField ? "field " : "method")} {parent}::{name} {sig}"); continue; }
    if (!HasMember(parent, name, sig, isField))
        missing.Add($"{(isField ? "field " : "method")} {parent}::{name} {sig}");
}

if (listOnly) return 0;
Console.WriteLine($"Checked {checkedCount} sts2 references from {Path.GetFileName(args[0])} against {args[1]}");
foreach (var m in missing) Console.WriteLine($"  MISSING {m}");
Console.WriteLine(missing.Count == 0 ? "OK: all references resolve." : $"{missing.Count} unresolved reference(s).");
return missing.Count == 0 ? 0 : 1;

// Renders signature types as assembly-agnostic full names so mod refs and game defs compare equal.
sealed class SigProvider(MetadataReader md) : ISignatureTypeProvider<string, object?>
{
    public string DefName(TypeDefinitionHandle h)
    {
        var td = md.GetTypeDefinition(h);
        var n = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        return decl.IsNil ? Join(md.GetString(td.Namespace), n) : DefName(decl) + "+" + n;
    }

    public string RefName(TypeReferenceHandle h)
    {
        var tr = md.GetTypeReference(h);
        var n = md.GetString(tr.Name);
        return tr.ResolutionScope.Kind == HandleKind.TypeReference
            ? RefName((TypeReferenceHandle)tr.ResolutionScope) + "+" + n
            : Join(md.GetString(tr.Namespace), n);
    }

    static string Join(string ns, string n) => ns.Length == 0 ? n : ns + "." + n;

    // Generic type definition name of a GenericInst TypeSpec (e.g. Foo`1<int> -> Foo`1).
    public string? GenericDefName(BlobHandle sig)
    {
        var r = md.GetBlobReader(sig);
        if ((SignatureTypeCode)r.ReadByte() != SignatureTypeCode.GenericTypeInstance) return null;
        r.ReadByte(); // class/valuetype
        var h = r.ReadTypeHandle();
        return h.Kind switch
        {
            HandleKind.TypeDefinition => DefName((TypeDefinitionHandle)h),
            HandleKind.TypeReference => RefName((TypeReferenceHandle)h),
            _ => null,
        };
    }

    public string? GameGenericDefName(BlobHandle sig, Func<TypeReferenceHandle, bool> isGame)
    {
        var r = md.GetBlobReader(sig);
        if ((SignatureTypeCode)r.ReadByte() != SignatureTypeCode.GenericTypeInstance) return null;
        r.ReadByte();
        var h = r.ReadTypeHandle();
        return h.Kind == HandleKind.TypeReference && isGame((TypeReferenceHandle)h) ? RefName((TypeReferenceHandle)h) : null;
    }

    public string GetArrayType(string e, ArrayShape s) => $"{e}[{new string(',', s.Rank - 1)}]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => $"{g}<{string.Join(",", a)}>";
    public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object? c, int i) => "!" + i;
    // Required modifiers are part of CLR signature matching (e.g. init-only setters carry
    // modreq(IsExternalInit)), so keep them; optional ones are not.
    public string GetModifiedType(string m, string u, bool req) => req ? $"{u} modreq({m})" : u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => DefName(h);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => RefName(h);
    public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) =>
        r.GetTypeSpecification(h).DecodeSignature(this, c);
}
