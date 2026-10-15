**Date:** September 4, 2026  
**Project Concept:** Cross-Agent Collaboration Hub (MCP Blackboard)

### **Recommended Project Names**

* **AgentMesh** (Clean, systems-level name)  
* **AgoraMCP** (Inspired by the Greek assembly place where thinkers bounced ideas)  
* **BlackboardMCP** (Matches the classic multi-agent CS blackboard design pattern)  
* **SynapseHub** (Biological metaphor for bridging different neural specialized nodes)  
* **CouncilMCP** (Directly captures the "AI advisory council" dynamic)

### **System Architecture & Core Mechanics**

In distributed systems, this is a **Shared Blackboard / Message Bus Pattern**. Instead of running a complex custom broker, an MCP server acts as the shared state coordinator that both IDE agents connect to concurrently via standard JSON-RPC.

Plaintext  
┌──────────────────────┐                     ┌──────────────────────┐  
│ Claude Code (Opus)   │                     │ ZCode / Cline (GLM)  │  
│ \[Agenting & High-Lvl\]│                     │ \[Low-Level & Kernel\] │  
└──────────┬───────────┘                     └──────────┬───────────┘  
           │ (MCP Client)                               │ (MCP Client)  
           ▼                                            ▼  
     ┌────────────────────────────────────────────────────────┐  
     │                AgoraMCP (Local MCP Server)             │  
     │  \- Thread Store (SQLite / In-Memory JSON)              │  
     │  \- Tools: post\_idea, consult\_peer, poll\_inbox, reply   │  
     └───────────────────────────┬────────────────────────────┘  
                                 │  
                                 ▼  
                     ┌──────────────────────┐  
                     │ Shared Context & Log │  
                     └──────────────────────┘

### **Core MCP Tools to Implement**

* **post\_question(topic, context, target\_specialty)**  
  * *Example:* Claude hits a GPU kernel bottleneck and posts: "Need optimal 16-token warp scan layout in Triton/CUDA", tagging target\_specialty="kernel".  
* **fetch\_pending\_queries(specialty)**  
  * ZCode/GLM 5.3 polls or reads pending topics tagged for its domain.  
* **submit\_peer\_review(thread\_id, critique\_or\_solution)**  
  * GLM writes the implementation review or kernel snippet back into the shared thread.  
* **read\_thread(thread\_id)**  
  * Claude reads the feedback, incorporates the code, and proceeds with repository editing.

### **Key Advantages**

* **Model Specialization:** Leverages Claude Opus for high-level refactoring and tool execution, Gemini for expansive context retrieval, and GLM for specialized hardware/kernel reasoning.  
* **Zero Direct Provider Coupling:** The models never need direct API keys to each other. They interact entirely through neutral MCP tool calls.  
* **Local Inspection:** Every bounced thought, peer review, and architecture decision is logged locally in human-readable Markdown or SQLite for later review.

Would you build the server in Python with FastMCP or in C\#/.NET to keep it aligned with your current stack?