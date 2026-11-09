using System.Reflection;
using NetArchTest.Rules;

namespace Serk.Clinic.ArchitectureTests;

/// <summary>Automated module dependency boundary checks (inspects compiled IL, including method bodies).</summary>
public class ModuleBoundaryTests
{
    private static readonly string[] Modules = ["Identity", "Patients", "Scheduling", "Clinical", "Documents"];

    private static Assembly Module(string name) => Assembly.Load("Serk.Clinic." + name);

    public static TheoryData<string> ModuleNames() => new(Modules);

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Domain_has_no_infrastructure_or_web_dependencies(string module)
    {
        var result = Types.InAssembly(Module(module))
            .That().ResideInNamespace($"Serk.Clinic.{module}.Domain")
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "Serk.Clinic.Platform", "System.Net.Http")
            .GetResult();
        Assert.True(result.IsSuccessful, "Violations: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Modules_only_use_other_modules_published_contracts(string module)
    {
        foreach (var other in Modules.Where(m => m != module))
        {
            var forbidden = new[] { "Domain", "Application", "Infrastructure", "Api" }.Select(layer => $"Serk.Clinic.{other}.{layer}").ToArray();
            var result = Types.InAssembly(Module(module)).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful, $"{module} reaches into {other} internals: " + string.Join(", ", result.FailingTypeNames ?? []));
        }
    }

    [Fact]
    public void Shared_kernel_depends_on_nothing_project_specific()
    {
        var result = Types.InAssembly(Assembly.Load("Serk.Clinic.SharedKernel"))
            .ShouldNot().HaveDependencyOnAny("Serk.Clinic.Platform", "Serk.Clinic.Identity", "Serk.Clinic.Patients", "Serk.Clinic.Clinical", "Serk.Clinic.Documents", "Serk.Clinic.Scheduling", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Platform_does_not_depend_on_business_modules()
    {
        var result = Types.InAssembly(Assembly.Load("Serk.Clinic.Platform"))
            .ShouldNot().HaveDependencyOnAny(Modules.Select(m => "Serk.Clinic." + m).ToArray())
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Contracts_do_not_expose_persistence_types()
    {
        foreach (var module in Modules)
        {
            var result = Types.InAssembly(Module(module)).That().ResideInNamespace($"Serk.Clinic.{module}.Contracts")
                .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Npgsql", $"Serk.Clinic.{module}.Infrastructure", $"Serk.Clinic.{module}.Domain")
                .GetResult();
            Assert.True(result.IsSuccessful, $"{module}: " + string.Join(", ", result.FailingTypeNames ?? []));
        }
    }

    [Fact]
    public void Only_documents_module_may_render_pdfs()
    {
        foreach (var module in Modules.Where(m => m != "Documents"))
        {
            var result = Types.InAssembly(Module(module)).ShouldNot().HaveDependencyOnAny("PdfSharp", "MigraDoc").GetResult();
            Assert.True(result.IsSuccessful, module);
        }
    }
}
