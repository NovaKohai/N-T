# Full Port of N&T to Rust

*   **Author:** NovaKohai
*   **Status:** Draft
*   **Date:** 2026-11-09

--------------------------------------------------------------------------------

## Overview

N&T is currently implemented as a Windows-only system composed of a .NET 8 HTTP multi-connection engine (`NT.Core`), a WPF desktop interface (`NT.App`), a Chromium native-messaging bridge (`NT.NativeHost`), and a MonoTorrent wrapper (`NT.Torrent`). 

This project re-architects and ports the entire system into a modular Rust workspace. The new architecture decouples the core networking engine, torrent pipeline, and native host into standalone crates communicating via local IPC with a cross-platform desktop frontend.

## The Problem

1. **Packaging and Binary Size**: Distributing a self-contained .NET 8 application requires bundling the runtime and WPF dependencies, producing an installer of approximately 94 MB. This increases distribution costs, download times, and update latency.
2. **Idle Memory Footprint**: The .NET runtime and Garbage Collector establish a memory baseline of 40 MB to 80 MB even when idling in the system tray. For a utility designed to stay active permanently in the background, this resource retention is excessive.
3. **Platform Confinement**: Relying on WPF confines the entire application exclusively to Windows. The engine cannot run as a lightweight daemon on headless servers, network-attached storage (NAS) devices, or non-Windows operating systems (Linux, macOS).
4. **Startup Latency**: Framework bootstrap and runtime JIT initialization create a measurable delay during cold startup, postponing IPC readiness when the browser extension dispatches capture requests.

## Goals

*   **Sub-20 MB Distribution Footprint**: Produce a release distribution with a total footprint under 20 MB without bundling an external runtime.
*   **Sub-15 MB Idle Memory**: Maintain a background resident set size (RSS) under 15 MB while active in the system tray with no active transfers.
*   **Deterministic Zero-GC Execution**: Eliminate runtime garbage collection to prevent frame drops, allocation spikes, and thread suspension during saturated multi-gigabit transfers.
*   **Client-Daemon Separation**: Isolate the core download and torrent engines into a headless background daemon (`nt-daemon`) that communicates over a versioned local socket or named pipe with client frontends.
*   **Cross-Platform Portability**: Ensure the core engine, CLI, and native host compile and operate natively on Windows, Linux, and macOS.
*   **Backward Compatibility**: Retain full wire-protocol compatibility with the existing Chromium MV3 extension (`extension/`) and parse the existing `queue.json` schema without manual migration.

## Non-Goals

*   **Rewriting the Browser Extension**: The existing Chromium Manifest V3 extension will not be rewritten; it will interface with the ported native host using identical JSON payloads over stdio.
*   **Embedding Multimedia Tools**: External CLI tools (`ffmpeg` and `yt-dlp`) will remain external processes invoked via process arguments rather than statically compiled libraries.
*   **Immediate Mobile Support**: Targeting Android or iOS clients is outside the scope of this project.

## Open Questions

*   **Desktop UI Toolkit Selection**: Whether to use Tauri (WebView-based UI) or an immediate/retained native Rust UI toolkit (such as Slint or Iced). Tauri provides fast UI development but introduces WebView memory overhead; native toolkits offer minimal memory but less mature accessibility and platform styling.
*   **BitTorrent Implementation Strategy**: Whether to bind `libtorrent-rasterbar` via C-FFI or adopt a native Rust BitTorrent implementation, balancing protocol maturity against compilation complexity.

## Critical User Journeys (CUJs)

### CUJ 1: Browser-Initiated Segmented Download

*   **Given**: The N&T daemon is running in the background, and the browser extension intercepts a downloadable HTTP resource.
*   **When**: The browser dispatches the download request to `nt-native-host`, which forwards it to the daemon over local IPC.
*   **Then**: The daemon validates byte ranges, initializes dynamic chunk workers, downloads data into staging shards, reassembles the file with zero intermediate memory copies, verifies checksums, and notifies the client interface.

### CUJ 2: Low-Footprint Background Queue Operation

*   **Given**: The user closes the desktop application window with pending or scheduled downloads remaining in the queue.
*   **When**: The main interface process exits, while the background daemon remains active in the system tray.
*   **Then**: The background daemon consumes less than 15 MB RSS, maintains persistent schedule timers, and restarts incomplete jobs at designated intervals without spawning a user interface.

### CUJ 3: Magnet Link and Torrent File Ingestion

*   **Given**: A user opens a `.torrent` file or clicks a `magnet:` link.
*   **When**: The application passes the metadata to `nt-torrent`.
*   **Then**: The engine resolves DHT/tracker endpoints, downloads torrent metadata, writes resume checkpoints to disk, and executes concurrent piece transfers while enforcing path validation to prevent directory traversal.
