using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TradingHub.Scripting;

namespace TradingHub.App.Services;

public sealed record IndicatorSourceDocument(string Id, string Text, string Attribution);
public sealed record IndicatorSourceCopy(string EntryPath, string TypeName, IReadOnlyList<string> Files);

/// <summary>
/// Exact metadata-type/source mapping and complete, isolated editable copies. The KVP source-view/fork
/// workflow is redesigned here with semantic symbols rather than textual class/constructor substitutions.
/// Original notices and comments remain attached to copied declarations.
/// </summary>
public sealed class IndicatorSources
{
    public const string ResourcePrefix = "TradingHub.IndicatorSource/";
    private static readonly ConcurrentDictionary<Assembly, IndicatorSources> Embedded = new();
    private readonly IReadOnlyDictionary<string, IndicatorSourceDocument> _documents;
    private readonly Lazy<CSharpCompilation> _compilation;

    public IndicatorSources(IEnumerable<IndicatorSourceDocument> documents) : this(documents, adaptCompatibleNamespaces: false) { }

    private IndicatorSources(IEnumerable<IndicatorSourceDocument> documents, bool adaptCompatibleNamespaces)
    {
        _documents = documents.ToDictionary(d => d.Id, StringComparer.Ordinal);
        // Built-ins are canonical source: never apply the legacy textual adapter to their comments, notices or literals.
        _compilation = new(() => ScriptCompiler.CreateCompilation(_documents.ToDictionary(p => p.Key, p => p.Value.Text),
            "TradingHubSourceIndex", adaptCompatibleNamespaces));
    }

    public static IndicatorSources ForAssembly(Assembly assembly) => Embedded.GetOrAdd(assembly, a =>
    {
        var documents = new List<IndicatorSourceDocument>();
        foreach (string resource in a.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = a.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            documents.Add(new(resource, reader.ReadToEnd(),
                $"{a.GetName().Name}: {resource}. Original authors and license terms are retained; see Trading Hub LICENSE and THIRD_PARTY_NOTICES.md."));
        }
        return new IndicatorSources(documents);
    });

    /// <summary>Returns every declaration document, including partial declarations; never guesses by filename.</summary>
    public IReadOnlyList<IndicatorSourceDocument> View(string metadataTypeName)
    {
        var symbol = FindType(metadataTypeName);
        return symbol.DeclaringSyntaxReferences.Select(r => _documents[r.SyntaxTree.FilePath]).DistinctBy(d => d.Id).ToArray();
    }

    /// <summary>
    /// Publishes all required source types in one directory move, only after the normal compiler can emit
    /// the complete result alongside existing user sources. No constructors or static initializers run.
    /// </summary>
    public IndicatorSourceCopy Fork(string metadataTypeName, string newClassName, string folder)
    {
        if (newClassName.StartsWith('@') || !SyntaxFacts.IsValidIdentifier(newClassName) || SyntaxFacts.GetKeywordKind(newClassName) != SyntaxKind.None)
            throw new ArgumentException("Choose a valid C# class name, not a keyword.", nameof(newClassName));
        var target = FindType(metadataTypeName);
        if (target.ContainingType != null || target.Arity != 0 || target.TypeKind != TypeKind.Class)
            throw new NotSupportedException("Copying a nested or generic indicator is not supported. No files were written.");

        var compilation = _compilation.Value;
        var closure = RequiredTypes(compilation, target);
        string prefix = "TradingHub.UserIndicators.Copy_" + Guid.NewGuid().ToString("N");
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        string? entryFile = null;
        int index = 0;
        foreach (var tree in compilation.SyntaxTrees.Where(t => _documents.ContainsKey(t.FilePath)))
        {
            if (!closure.Any(t => t.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == tree))) continue;
            var document = _documents[tree.FilePath];
            var model = compilation.GetSemanticModel(tree);
            var rewritten = (CompilationUnitSyntax)new CopyRewriter(model, closure, target, newClassName, prefix).Visit(tree.GetRoot())!;
            if (rewritten.Members.OfType<BaseTypeDeclarationSyntax>().Any() || rewritten.Members.OfType<DelegateDeclarationSyntax>().Any())
                rewritten = rewritten.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                    SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(prefix)).WithMembers(rewritten.Members)));
            // Native projects use SDK implicit imports; keep their ordinary imports explicit in editable source.
            var imports = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in closure.Where(t => t.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == tree)))
                for (var ns = type.ContainingNamespace; !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
                    if (HasReferencedNamespace(compilation, ns.ToDisplayString())) imports.Add(ns.ToDisplayString());
            foreach (string ns in imports.Concat(new[] { "System", "System.IO", "System.Threading", "System.Threading.Tasks", "System.Collections.Generic", "System.Linq" }))
                if (!rewritten.Usings.Any(u => u.Alias == null && u.Name?.ToString() == ns))
                    rewritten = rewritten.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns)));
            string file = $"{++index:000}_{Path.GetFileName(tree.FilePath.Replace('\\', '/'))}";
            string attribution = document.Attribution.Replace("\r", " ").Replace("\n", " ");
            sources.Add(file, $"// Editable copy of {metadataTypeName}. The built-in remains unchanged.\n// Source attribution: {attribution}\n" + rewritten.NormalizeWhitespace().ToFullString() + "\n");
            if (target.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == tree)) entryFile ??= file;
        }

        string root = Path.GetFullPath(folder);
        var existing = new IndicatorSourceFiles(root).ReadAll();
        var preflight = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        foreach (var (file, text) in sources) preflight.Add(Path.Combine(root, ".source-copy-preflight", file), text);
        var validation = ScriptCompiler.ValidateSources(preflight);
        if (!validation.Success)
            throw new NotSupportedException("This source and its dependencies cannot be copied safely. No files were written.\n" +
                string.Join("\n", validation.Diagnostics.Where(d => d.Severity == "error").Take(12).Select(d => d.ToString())));

        Directory.CreateDirectory(root);
        string destination = UniqueDirectory(root, newClassName);
        // Staging is a sibling of the watched root: the watcher never sees half a dependency closure.
        string stage = Path.Combine(Path.GetDirectoryName(root)!, ".indicator-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var (file, text) in sources) File.WriteAllText(Path.Combine(stage, file), text, new UTF8Encoding(false));
            Directory.Move(stage, destination); // no overwrite; a concurrent collision fails safely
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
        string typeName = prefix + "." + (target.ContainingNamespace.IsGlobalNamespace ? "" : target.ContainingNamespace.ToDisplayString() + ".") + newClassName;
        return new(Path.Combine(destination, entryFile!), typeName, sources.Keys.Select(f => Path.Combine(destination, f)).ToArray());
    }

    public static IReadOnlyList<string> UserFiles(Type type, string folder)
    {
        var sources = new IndicatorSourceFiles(folder).ReadAll();
        if (sources.Count == 0) return Array.Empty<string>();
        var index = new IndicatorSources(sources.Select(p => new IndicatorSourceDocument(p.Key, p.Value, "User-owned source")), adaptCompatibleNamespaces: true);
        try { return index.View(type.FullName!).Select(d => d.Id).ToArray(); }
        catch (NotSupportedException) { return Array.Empty<string>(); }
    }

    private INamedTypeSymbol FindType(string metadataTypeName)
    {
        var type = _compilation.Value.GetTypeByMetadataName(metadataTypeName);
        if (type == null || !type.DeclaringSyntaxReferences.Any(r => _documents.ContainsKey(r.SyntaxTree.FilePath)))
            throw new NotSupportedException("The exact source for this indicator is not embedded or is ambiguous. No source file was changed.");
        return type;
    }

    private static bool HasReferencedNamespace(CSharpCompilation compilation, string name)
    {
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            INamespaceSymbol? current = assembly.GlobalNamespace;
            foreach (string part in name.Split('.')) current = current?.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (current != null) return true;
        }
        return false;
    }

    private static string UniqueDirectory(string root, string name)
    {
        var existing = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; ; i++)
        {
            string candidate = i == 1 ? name : name + "_" + i;
            if (!existing.Contains(candidate)) return Path.Combine(root, candidate);
        }
    }

    private static HashSet<INamedTypeSymbol> RequiredTypes(CSharpCompilation compilation, INamedTypeSymbol target)
    {
        var closure = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Queue<INamedTypeSymbol>();
        Add(target);
        while (pending.TryDequeue(out var current))
            foreach (var reference in current.DeclaringSyntaxReferences)
            {
                var model = compilation.GetSemanticModel(reference.SyntaxTree);
                foreach (var node in reference.GetSyntax().DescendantNodesAndSelf())
                {
                    // Both type references and members matter: static helpers and extension methods carry dependencies.
                    if (node is not (NameSyntax or MemberAccessExpressionSyntax or AttributeSyntax or ConstructorInitializerSyntax)) continue;
                    var info = model.GetSymbolInfo(node);
                    AddSymbol(info.Symbol);
                    foreach (var candidate in info.CandidateSymbols) AddSymbol(candidate);
                }
            }
        return closure;

        void AddSymbol(ISymbol? symbol)
        {
            if (symbol is IAliasSymbol alias) symbol = alias.Target;
            if (symbol is IMethodSymbol { ReducedFrom: { } extension } && extension.ContainingType.DeclaringSyntaxReferences.Any())
                throw new NotSupportedException("This source uses a source-defined extension-method dependency. Automatic copying of that call binding is unsupported; no files were written.");
            if (symbol is INamedTypeSymbol type) Add(type);
            else if (symbol?.ContainingType is { } containing) Add(containing);
        }
        void Add(INamedTypeSymbol symbol)
        {
            while (symbol.ContainingType != null) symbol = symbol.ContainingType;
            symbol = symbol.OriginalDefinition;
            if (!symbol.DeclaringSyntaxReferences.Any() || !closure.Add(symbol)) return;
            pending.Enqueue(symbol);
        }
    }

    private sealed class CopyRewriter(SemanticModel model, HashSet<INamedTypeSymbol> closure,
        INamedTypeSymbol target, string newName, string prefix) : CSharpSyntaxRewriter
    {
        private bool Copied(INamedTypeSymbol type)
        {
            while (type.ContainingType != null) type = type.ContainingType;
            return closure.Contains(type.OriginalDefinition);
        }
        private string TypeName(ITypeSymbol symbol)
        {
            if (symbol is IArrayTypeSymbol array) return TypeName(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]";
            if (symbol is IPointerTypeSymbol pointer) return TypeName(pointer.PointedAtType) + "*";
            if (symbol is not INamedTypeSymbol type || !ContainsCopied(type)) return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (type.IsTupleType) return "(" + string.Join(", ", type.TupleElements.Select(e => TypeName(e.Type) + " " + e.Name)) + ")";
            string name = SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, target) ? newName : type.Name;
            if (SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None) name = "@" + name;
            string parent = type.ContainingType != null ? TypeName(type.ContainingType) : "global::" +
                (Copied(type) ? prefix + (type.ContainingNamespace.IsGlobalNamespace ? "" : ".") : "") +
                (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString());
            return parent + (parent.EndsWith("::", StringComparison.Ordinal) ? "" : ".") + name +
                (type.Arity == 0 ? "" : "<" + string.Join(", ", type.TypeArguments.Select(TypeName)) + ">");
        }
        private bool ContainsCopied(ITypeSymbol symbol) => symbol switch
        {
            IArrayTypeSymbol array => ContainsCopied(array.ElementType),
            IPointerTypeSymbol pointer => ContainsCopied(pointer.PointedAtType),
            INamedTypeSymbol type => Copied(type) || type.TypeArguments.Any(ContainsCopied) ||
                (type.ContainingType != null && ContainsCopied(type.ContainingType)),
            _ => false,
        };
        private INamedTypeSymbol? ReferencedType(SyntaxNode node)
        {
            var symbol = model.GetSymbolInfo(node).Symbol;
            if (symbol is IAliasSymbol alias) symbol = alias.Target;
            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor) symbol = constructor.ContainingType;
            return symbol is INamedTypeSymbol type && Copied(type) ? type : null;
        }
        public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node) => ReferencedType(node) is { } type
            ? SyntaxFactory.ParseName(TypeName(type)).WithTriviaFrom(node) : base.VisitQualifiedName(node);
        public override SyntaxNode? VisitAliasQualifiedName(AliasQualifiedNameSyntax node) => ReferencedType(node) is { } type
            ? SyntaxFactory.ParseName(TypeName(type)).WithTriviaFrom(node) : base.VisitAliasQualifiedName(node);
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node) => ReferencedType(node) is { } type
            ? SyntaxFactory.ParseExpression(TypeName(type)).WithTriviaFrom(node) : base.VisitMemberAccessExpression(node);
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) => ReferencedType(node) is { } type
            ? SyntaxFactory.ParseName(TypeName(type)).WithTriviaFrom(node) : base.VisitIdentifierName(node);
        public override SyntaxNode? VisitGenericName(GenericNameSyntax node) => ReferencedType(node) is { } type
            ? SyntaxFactory.ParseName(TypeName(type)).WithTriviaFrom(node) : base.VisitGenericName(node);

        public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            var visited = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node)!;
            return node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Any() ? visited :
                visited.WithName(SyntaxFactory.ParseName(prefix + "." + node.Name).WithTriviaFrom(node.Name));
        }
        public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node) =>
            ((FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!).WithName(
                SyntaxFactory.ParseName(prefix + "." + node.Name).WithTriviaFrom(node.Name));

        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => RewriteType(node, () => base.VisitClassDeclaration(node));
        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => RewriteType(node, () => base.VisitStructDeclaration(node));
        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => RewriteType(node, () => base.VisitRecordDeclaration(node));
        public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => RewriteType(node, () => base.VisitInterfaceDeclaration(node));
        public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node) => RewriteType(node, () => base.VisitEnumDeclaration(node));
        public override SyntaxNode? VisitDelegateDeclaration(DelegateDeclarationSyntax node)
        {
            var symbol = model.GetDeclaredSymbol(node)!;
            return symbol.ContainingType == null && !Copied(symbol) ? null : base.VisitDelegateDeclaration(node);
        }
        private SyntaxNode? RewriteType(BaseTypeDeclarationSyntax node, Func<SyntaxNode?> visit)
        {
            var symbol = model.GetDeclaredSymbol(node)!;
            if (symbol.ContainingType == null && !Copied(symbol)) return null;
            var result = (BaseTypeDeclarationSyntax)visit()!;
            var first = symbol.DeclaringSyntaxReferences[0];
            bool firstDeclaration = first.SyntaxTree == node.SyntaxTree && first.Span == node.Span;
            if (SymbolEqualityComparer.Default.Equals(symbol, target))
            {
                result = result.WithIdentifier(SyntaxFactory.Identifier(newName).WithTriviaFrom(node.Identifier));
                if (firstDeclaration && !HasAttribute(symbol, "System.ComponentModel.DisplayNameAttribute"))
                    result = result.AddAttributeLists(Attribute("global::System.ComponentModel.DisplayName",
                        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(newName))));
            }
            else if (symbol.TypeKind == TypeKind.Class && firstDeclaration && !HasAttribute(symbol, "System.ComponentModel.BrowsableAttribute"))
                result = result.AddAttributeLists(Attribute("global::System.ComponentModel.Browsable", SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)));
            return result;
        }
        private static AttributeListSyntax Attribute(string name, ExpressionSyntax argument) =>
            SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(
                SyntaxFactory.ParseName(name), SyntaxFactory.AttributeArgumentList(
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.AttributeArgument(argument))))));
        private static bool HasAttribute(INamedTypeSymbol symbol, string name) => symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == name);
        public override SyntaxNode? VisitAttribute(AttributeSyntax node)
        {
            var symbol = model.GetSymbolInfo(node).Symbol?.ContainingType;
            var owner = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            bool selected = owner != null && SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(owner), target);
            ExpressionSyntax? replacement = selected && symbol?.ToDisplayString() == "System.ComponentModel.DisplayNameAttribute"
                ? SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(newName))
                : !selected && symbol?.ToDisplayString() == "System.ComponentModel.BrowsableAttribute"
                    ? SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression) : null;
            if (replacement != null)
                return node.WithArgumentList(SyntaxFactory.AttributeArgumentList(
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.AttributeArgument(replacement))));
            return base.VisitAttribute(node);
        }
        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
            SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(node)?.ContainingType, target)
                ? ((ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!).WithIdentifier(SyntaxFactory.Identifier(newName).WithTriviaFrom(node.Identifier))
                : base.VisitConstructorDeclaration(node);
        public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node) =>
            SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(node)?.ContainingType, target)
                ? ((DestructorDeclarationSyntax)base.VisitDestructorDeclaration(node)!).WithIdentifier(SyntaxFactory.Identifier(newName).WithTriviaFrom(node.Identifier))
                : base.VisitDestructorDeclaration(node);
    }
}
