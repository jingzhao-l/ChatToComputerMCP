import AppKit

/// Local-only history inspector. It reads atomically published records, not MCP
/// tool responses, and works with a disconnected gateway.
final class CommandHistoryViewController: NSViewController, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate {
    private let profile: String
    private let table = NSTableView()
    private let search = NSSearchField()
    private let status = NSTextField(wrappingLabelWithString: "")
    private let details = NSTextView()
    private let exportButton = NSButton(title: "Export selected…", target: nil, action: nil)
    private var records: [CommandHistoryRecord] = []
    private var filtered: [CommandHistoryRecord] = []
    private var refreshGeneration = 0
    private let dateFormat: DateFormatter = {
        let f = DateFormatter(); f.dateStyle = .short; f.timeStyle = .medium; return f
    }()

    init(profile: String) {
        self.profile = profile
        super.init(nibName: nil, bundle: nil)
    }
    required init?(coder: NSCoder) { nil }

    override func loadView() {
        view = NSView(frame: NSRect(x: 0, y: 0, width: 820, height: 640))
        let heading = NSTextField(labelWithString: "Command history")
        heading.font = .systemFont(ofSize: 23, weight: .semibold)
        let explanation = NSTextField(wrappingLabelWithString:
            "Local profile: \(profile). Up to \(CommandHistoryStore.maxCompleted) completed records, retained \(CommandHistoryStore.retentionDays) days. Command text and output are saved only when explicitly enabled. A recorded running state is not proof of a live process.")
        explanation.textColor = .secondaryLabelColor
        search.placeholderString = "Search workspace, directory, command or status"
        search.delegate = self
        search.setAccessibilityLabel("Search command history")
        let refresh = NSButton(title: "Refresh", target: self, action: #selector(refreshRecords))
        exportButton.target = self
        exportButton.action = #selector(exportSelected)
        exportButton.isEnabled = false
        let actions = NSStackView(views: [search, refresh, exportButton])
        actions.orientation = .horizontal
        actions.spacing = 8
        search.setContentHuggingPriority(.defaultLow, for: .horizontal)
        for (id, title, width) in [("started", "Started", 165), ("workspace", "Workspace", 170),
                                  ("kind", "Type", 75), ("state", "Status", 130), ("exit", "Exit", 55)] {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id))
            column.title = title; column.width = CGFloat(width); column.minWidth = 45
            table.addTableColumn(column)
        }
        table.dataSource = self; table.delegate = self
        table.usesAlternatingRowBackgroundColors = true
        table.allowsMultipleSelection = false
        table.setAccessibilityLabel("Local command history")
        let listScroll = NSScrollView()
        listScroll.documentView = table
        listScroll.hasVerticalScroller = true
        listScroll.hasHorizontalScroller = true
        details.isEditable = false; details.isSelectable = true
        details.font = .monospacedSystemFont(ofSize: 12, weight: .regular)
        details.textContainerInset = NSSize(width: 10, height: 10)
        let detailScroll = NSScrollView()
        detailScroll.documentView = details
        detailScroll.hasVerticalScroller = true
        detailScroll.borderType = .bezelBorder
        status.textColor = .secondaryLabelColor
        let stack = NSStackView(views: [heading, explanation, actions, status, listScroll, detailScroll])
        stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 10
        stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 18),
            stack.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -18),
            stack.topAnchor.constraint(equalTo: view.topAnchor, constant: 18),
            stack.bottomAnchor.constraint(equalTo: view.bottomAnchor, constant: -18),
            explanation.widthAnchor.constraint(equalTo: stack.widthAnchor),
            actions.widthAnchor.constraint(equalTo: stack.widthAnchor),
            status.widthAnchor.constraint(equalTo: stack.widthAnchor),
            listScroll.widthAnchor.constraint(equalTo: stack.widthAnchor),
            listScroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 170),
            listScroll.heightAnchor.constraint(equalTo: detailScroll.heightAnchor, multiplier: 0.9),
            detailScroll.widthAnchor.constraint(equalTo: stack.widthAnchor),
            detailScroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 120)
        ])
        refreshRecords()
    }

    @objc private func refreshRecords() {
        refreshGeneration += 1
        let generation = refreshGeneration
        let directory = CommandHistoryStore.directory(for: profile)
        status.stringValue = "Reading local history…"
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let result = Result { try CommandHistoryStore.read(directory: directory) }
            DispatchQueue.main.async {
                guard let self, self.refreshGeneration == generation else { return }
                switch result {
                case .success(let rows):
                    self.records = rows; self.applyFilter()
                case .failure(let error):
                    self.status.stringValue = "History unavailable: " + error.localizedDescription
                }
            }
        }
    }

    private var selected: CommandHistoryRecord? {
        filtered.indices.contains(table.selectedRow) ? filtered[table.selectedRow] : nil
    }

    private func applyFilter() {
        let old = selected?.id
        let q = search.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        filtered = records.filter { row in
            q.isEmpty || [row.workspaceName, row.workspaceID, row.cwd, row.kind, row.state,
                          row.requestID ?? "", row.command ?? ""].contains { $0.localizedCaseInsensitiveContains(q) }
        }
        table.reloadData()
        if let index = filtered.firstIndex(where: { $0.id == old }) {
            table.selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false)
        } else if !filtered.isEmpty {
            table.selectRowIndexes(IndexSet(integer: 0), byExtendingSelection: false)
        }
        status.stringValue = "\(filtered.count) of \(records.count) records"
        showSelection()
    }

    func controlTextDidChange(_ obj: Notification) { applyFilter() }
    func numberOfRows(in tableView: NSTableView) -> Int { filtered.count }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard filtered.indices.contains(row), let column = tableColumn else { return nil }
        let item = filtered[row]
        let text: String
        switch column.identifier.rawValue {
        case "started": text = dateFormat.string(from: item.startedAt)
        case "workspace": text = item.workspaceName
        case "kind": text = item.kind == "sync" ? "Short" : "Long"
        case "state": text = item.state
        default: text = item.exitCode.map(String.init) ?? "—"
        }
        let label = NSTextField(labelWithString: text)
        label.lineBreakMode = .byTruncatingTail
        return label
    }
    func tableViewSelectionDidChange(_ notification: Notification) { showSelection() }

    private func showSelection() {
        exportButton.isEnabled = selected != nil
        guard let item = selected else { details.string = "No matching command records."; return }
        let elapsed = max(0, item.updatedAt.timeIntervalSince(item.startedAt))
        details.string = """
        Workspace: \(item.workspaceName) (\(item.workspaceID))
        Directory: \(item.cwd)
        Type: \(item.kind)
        Request: \(item.requestID ?? "Synchronous call")
        Client: \(item.clientID ?? "Shared/local connection — no verified individual client")
        Started: \(dateFormat.string(from: item.startedAt))
        Last updated: \(dateFormat.string(from: item.updatedAt))
        Recorded duration: \(String(format: "%.2f", elapsed))s
        State: \(item.state)
        Exit: \(item.exitCode.map(String.init) ?? "Unconfirmed")
        \(item.truncated ? "Some captured content was truncated." : "")

        COMMAND
        \(item.command ?? "[Content capture was not enabled]")

        OUTPUT
        \(item.output ?? "[No captured output]")
        """
    }

    @objc private func exportSelected() {
        guard let item = selected else { return }
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "command-\(item.id.uuidString.lowercased()).json"
        panel.message = "The exported record may contain credentials or private command output. Review before sharing."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(item).write(to: url, options: .atomic)
            status.stringValue = "Selected record exported."
        } catch { status.stringValue = "Export failed: " + error.localizedDescription }
    }
}
