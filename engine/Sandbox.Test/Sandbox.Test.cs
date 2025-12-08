global using Microsoft.VisualStudio.TestTools.UnitTesting;
global using Sandbox;
global using System.Linq;
global using System.Threading.Tasks;
global using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

[TestClass]
public class TestInit
{
	public static Sandbox.AppSystem TestAppSystem;

	[AssemblyInitialize]
	public static void ClassInitialize( TestContext context )
	{
		TestAppSystem = new TestAppSystem();
		TestAppSystem.Init();
	}

	[AssemblyCleanup]
	public static void AssemblyCleanup()
	{
		TestAppSystem.Shutdown();
	}
}
