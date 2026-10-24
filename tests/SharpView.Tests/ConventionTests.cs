using SharpView.Core;
using Xunit;

namespace SharpView.Tests;

// Classification from facts alone: base classes and overridden members may live in referenced assemblies.
public sealed class ConventionTests
{
    private static readonly CompilationId Source = new("source"), Metadata = new("metadata|Mvc");
    private static readonly TypeUse Void = new(new TypeId("Void"), "Void", NullabilityKind.NotAnnotated, []);
    private static AttributeFact Attribute(string name) => new(name, [], []);
    private static TypeFact Type(string name, CompilationId compilation, string? baseClass = null, TypeModifiers modifiers = TypeModifiers.None, params string[] attributes) =>
        new(new(name), compilation, name, "Public", [], [.. attributes.Select(Attribute)], [], "", DeclaredTypeKind.Class, modifiers, baseClass is null ? null : new TypeId(baseClass));
    private static CallableFact Method(string name, string type, CompilationId compilation, CallableModifiers modifiers = CallableModifiers.None,
        string? overrides = null, string accessibility = "Public", params string[] attributes) =>
        new(new(name), compilation, new TypeId(type), null, CallableKind.Method, name + "()", accessibility, Void, [], [.. attributes.Select(Attribute)],
            compilation == Metadata ? BodyStatus.MetadataOnly : BodyStatus.Source, [], Modifiers: modifiers, Overrides: overrides is null ? null : new CallableId(overrides));

    [Fact]
    public void Controller_actions_follow_base_class_and_override_chains_through_metadata()
    {
        var index = new ProgramIndex(new ProgramSnapshot
        {
            Types = [Type("Microsoft.AspNetCore.Mvc.ControllerBase", Metadata), Type("VendorBase", Metadata, "Microsoft.AspNetCore.Mvc.ControllerBase"),
                Type("Orders", Source, "VendorBase"), Type("Hidden", Source, "Excluded"), Type("Excluded", Source, "Microsoft.AspNetCore.Mvc.ControllerBase", TypeModifiers.None, "Microsoft.AspNetCore.Mvc.NonControllerAttribute"),
                Type("Abstract", Source, "Microsoft.AspNetCore.Mvc.ControllerBase", TypeModifiers.Abstract), Type("Plain", Source)],
            Callables = [Method("VendorBase.Helper", "VendorBase", Metadata, CallableModifiers.Virtual, attributes: "Microsoft.AspNetCore.Mvc.NonActionAttribute"),
                Method("Orders.Helper", "Orders", Source, CallableModifiers.Override, "VendorBase.Helper"), Method("Orders.Get", "Orders", Source),
                Method("Orders.Static", "Orders", Source, CallableModifiers.Static), Method("Orders.Internal", "Orders", Source, accessibility: "Internal"),
                Method("Hidden.Get", "Hidden", Source), Method("Abstract.Get", "Abstract", Source), Method("Plain.Get", "Plain", Source)]
        });
        var action = Assert.Single(AspNetConventions.ControllerActions(index));
        Assert.Equal(new CallableId("Orders.Get"), action.Method);
        Assert.False(AspNetConventions.IsControllerAction(index, index.Callable(new("VendorBase.Helper"))));
    }

    [Fact]
    public void Global_handlers_cover_actions_of_the_registering_project_and_of_projects_it_references()
    {
        static CompilationFact Project(string name, params string[] references) =>
            new(new(name), $"/src/{name}.csproj", name, "net10.0", "Debug", "Preview", "Enable", [], []) { ProjectReferences = [.. references.Select(x => $"/src/{x}.csproj")] };
        static BoundaryHandlingFact Registration(string project, string name) => new(new(project), name, name, new($"/src/{project}/Program.cs", "", 0, 1, 1, 1, false));
        var index = new ProgramIndex(new ProgramSnapshot
        {
            Compilations = [Project("Host", "Web"), Project("Web", "Shared"), Project("Shared"), Project("Other"), Project("Worker")],
            Callables = [Method("Web.Get", "T", new("Web")), Method("Shared.Get", "T", new("Shared")), Method("Other.Get", "T", new("Other")), Method("Host.Get", "T", new("Host"))],
            BoundaryHandling = [Registration("Host", "UseExceptionHandler"), Registration("Other", "AddExceptionHandler"), Registration("Worker", "UseDeveloperExceptionPage")],
        });
        string[] Handlers(string action) => [.. AspNetConventions.GlobalHandlers(index, new(action)).Select(x => x.Registration)];
        Assert.Equal(["UseExceptionHandler"], Handlers("Host.Get"));
        Assert.Equal(["UseExceptionHandler"], Handlers("Web.Get"));
        Assert.Equal(["UseExceptionHandler"], Handlers("Shared.Get"));
        // AddExceptionHandler alone handles nothing; a registration in an unrelated project does not run these actions.
        Assert.Empty(Handlers("Other.Get"));
    }
}
