using CareerMentor.Api.Domain;
using CareerMentor.Api.Infra;
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace CareerMentor.Api.Plugins;

public class CareerGoalsPlugin(Context db)
{
    [KernelFunction("save_goal")]
    [Description("Salva um objetivo de carreira com horizonte e métrica")]
    public async Task<string> SaveGoalAsync(string title, string horizon, string metric)
    {
        var goal = new Goal { Title = title, Horizon = horizon, Metric = metric };
        db.Goals.Add(goal);
        await db.SaveChangesAsync();
        return goal.Id.ToString();
    }

    [KernelFunction("add_next_action")]
    [Description("Registra uma próxima ação para um objetivo existente")]
    public async Task<string> AddNextActionAsync(Guid goalId, string title, int effortHours, DateTime? dueDate = null)
    {
        var action = new NextAction { GoalId = goalId, Title = title, EffortHours = effortHours, DueDate = dueDate };
        db.NextActions.Add(action);
        await db.SaveChangesAsync();
        return action.Id.ToString();
    }
}
