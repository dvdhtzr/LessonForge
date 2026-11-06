// Copyright (c) 2026 — LessonForge. Original work.

using System.Net.Http.Headers;
using LessonForge.Core.Agents;
using LessonForge.Core.Configuration;
using LessonForge.Core.Dto;
using LessonForge.Core.Rag;
using Microsoft.Agents.AI.Workflows;
using MafWorkflow = Microsoft.Agents.AI.Workflows.Workflow;

namespace LessonForge.Core.Workflow;

// Lesson Design Crew — workflow orchestrator
//   LessonRequest ──► Research ──► Lesson Planner ──► Quiz Generator ──► Fact-Check ──► VerifiedLessonOutput
// Interactive builds insert approval gates after Research and after the Lesson Plan.
public static class LessonWorkflow
{
    /// <summary>The stage names, in order, for UI progress rendering.</summary>
    public static readonly IReadOnlyList<string> StageOrder =
        ["Research", "PerspectiveAnalysis", "LessonPlanner", "QuizGenerator", "FactCheck"];

    /// <summary>
    /// Builds the lesson-design workflow. When <paramref name="interactive"/> is true, two
    /// <see cref="RequestPort"/> gates are inserted so a host can approve/edit the research scope
    /// and the lesson plan before the run continues. The returned <see cref="RagContext"/> is shared
    /// by every executor and carries the live retrieval session.
    /// </summary>
    public static (MafWorkflow Workflow, RagContext Rag) Build(
        ClientFactory clients, RetrievalService retrieval, bool interactive,
        IProgress<string>? researchProgress = null, bool explorePerspectives = false)
    {
        var chat = clients.CreateChatClient();
        var rag = new RagContext();
        var maxOutputTokens = clients.Options.MaxOutputTokens;

        var research = new ResearchAgent.ResearchExecutor(ResearchAgent.CreateAgent(chat, maxOutputTokens), retrieval, rag, researchProgress);
        var perspectives = explorePerspectives
            ? new PerspectiveAnalystAgent.PerspectiveAnalysisExecutor(
                PerspectiveAnalystAgent.CreateAgent(chat, maxOutputTokens), retrieval, rag)
            : null;
        var planner = new LessonPlannerAgent.LessonPlannerExecutor(LessonPlannerAgent.CreateAgent(chat, maxOutputTokens), rag);
        var quiz = new QuizGeneratorAgent.QuizGeneratorExecutor(QuizGeneratorAgent.CreateAgent(chat, maxOutputTokens), rag);
        var factcheck = new FactCheckAgent.FactCheckExecutor(FactCheckAgent.CreateAgent(chat, maxOutputTokens), rag);

        // Four explicit graphs keep the standard (toggle-off) flow exactly as before.
        // The optional agent is between factual research and the existing educator
        // research gate, so approval covers both facts and identified viewpoints.
        if (!interactive && perspectives is null)
        {
            var workflow = new WorkflowBuilder(research)
                .AddEdge(research, planner)
                .AddEdge(planner, quiz)
                .AddEdge(quiz, factcheck)
                .WithOutputFrom(factcheck)
                .WithName("lesson-design-crew")
                .Build();
            return (workflow, rag);
        }

        if (!interactive)
        {
            var workflow = new WorkflowBuilder(research)
                .AddEdge(research, perspectives!)
                .AddEdge(perspectives!, planner)
                .AddEdge(planner, quiz)
                .AddEdge(quiz, factcheck)
                .WithOutputFrom(factcheck)
                .WithName("lesson-design-crew-perspectives")
                .Build();
            return (workflow, rag);
        }

        var approveResearch = RequestPort.Create<ResearchOutput, ResearchOutput>("approve-research");
        var approvePlan = RequestPort.Create<LessonPlanOutput, LessonPlanOutput>("approve-plan");

        if (perspectives is null)
        {
            var workflow = new WorkflowBuilder(research)
                .AddEdge(research, approveResearch)
                .AddEdge(approveResearch, planner)
                .AddEdge(planner, approvePlan)
                .AddEdge(approvePlan, quiz)
                .AddEdge(quiz, factcheck)
                .WithOutputFrom(factcheck)
                .WithName("lesson-design-crew-interactive")
                .Build();
            return (workflow, rag);
        }

        var perspectiveWorkflow = new WorkflowBuilder(research)
            .AddEdge(research, perspectives)
            .AddEdge(perspectives, approveResearch)
            .AddEdge(approveResearch, planner)
            .AddEdge(planner, approvePlan)
            .AddEdge(approvePlan, quiz)
            .AddEdge(quiz, factcheck)
            .WithOutputFrom(factcheck)
            .WithName("lesson-design-crew-perspectives-interactive")
            .Build();
        return (perspectiveWorkflow, rag);
    }

    /// <summary>
    /// Convenience factory: wires a <see cref="RetrievalService"/> from a <see cref="ClientFactory"/>,
    /// creating HTTP clients with a generic, polite User-Agent. Hosts with their own DI can build the
    /// pieces directly instead.
    /// </summary>
    public static RetrievalService CreateRetrievalService(ClientFactory clients, RetrievalOptions? options = null)
    {
        var wikipedia = new WikipediaClient(CreateHttpClient());
        var fetcher = new SourceFetcher(CreateHttpClient());
        var embeddings = clients.CreateEmbeddingGenerator();
        var registry = new SourceRegistry(
        [
            new UsgsEarthquakeProvider(CreateHttpClient()),
            new LocLawmakingCatalogProvider(CreateHttpClient()),
            new GutenbergCatalogProvider(CreateHttpClient()),
            new SmithsonianOpenAccessMetadataProvider(CreateHttpClient())
        ]);
        return new RetrievalService(wikipedia, fetcher, embeddings, options, registry: registry);
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LessonForge/1.0 (educational lesson generator)");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return http;
    }
}
