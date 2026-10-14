using System.ComponentModel;

namespace AgenticUI.AgentServer.Scenarios.SharedState;

/// <summary>
/// The backend tool the shared-state agent calls. It simply returns the recipe the model produced;
/// the local client projects that result into a <c>StateSnapshotEvent</c> for the Blazor state mapper.
/// </summary>
internal static class RecipeTools
{
    [Description("Generate or update the shared recipe and display it to the user.")]
    public static RecipeResponse GenerateRecipe(
        [Description("The complete recipe to display.")] Recipe recipe) => new() { Recipe = recipe };
}
