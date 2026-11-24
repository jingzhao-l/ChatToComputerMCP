import AppKit

final class ActivityAuditViewController: NSViewController, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate {
    private let profile: String
    private let search = NSSearchField()
    private let table = NSTableView()
    private let status = NSTextField(labelWithString: "")
    private var rows: [ActivityAuditEvent] = []
    private var visible: [ActivityAuditEvent] = []
    private var generation = 0
    private let export = NSButton(title: "Export filtered…", target: nil, action: nil)
    private let formatter: DateFormatter = {
        let date = DateFormatter(); date.dateStyle = .short; date.timeStyle = .medium; return date
    }()

    init(profile: String) { self.profile = profile; super.init(nibName: nil, bundle: nil) }
    required init?(coder: NSCoder) { nil }

    override func loadView() {
        view = NSView(frame: NSRect(x: 0, y: 0, width: 900, height: 520))
        let heading = NSTextField(labelWithString: "Activity audit")
        heading.font = .systemFont(ofSize: 23, weight: .semibold)
        let help = NSTextField(wrappingLabelWithString: "Local profile: \(profile). Search tool calls, workspace IDs, verified client IDs and outcomes. No command contents, arguments or result text are recorded. The two latest bounded audit segments are retained.")
        help.textColor = .secondaryLabelColor
        search.placeholderString = "Search tool, workspace, client or outcome"
        search.delegate = self
        let refresh = NSButton(title: "Refresh", target: self, action: #selector(reload))
        export.target = self; export.action = #selector(exportRows); export.isEnabled = false
        let actions = NSStackView(views: [search, refresh, export])
        actions.orientation = .horizontal; actions.spacing = 8
        for (id, name, width) in [("time","Time",165),("tool","Tool",175),("scope","Workspace",110),("client","Verified client",180),("result","Outcome",80),("duration","ms",65),("bytes","Bytes",70)] {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id))
            column.title = name; column.width = CGFloat(width); column.minWidth = 45
            table.addTableColumn(column)
        }
        table.dataSource = self; table.delegate = self; table.usesAlternatingRowBackgroundColors = true
        table.setAccessibilityLabel("Activity audit events")
        let scroll = NSScrollView(); scroll.documentView = table
        scroll.hasVerticalScroller = true; scroll.hasHorizontalScroller = true
        let stack = NSStackView(views: [heading, help, actions, status, scroll])
        stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 10
        stack.translatesAutoresizingMaskIntoConstraints = false; view.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 18),
            stack.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -18),
            stack.topAnchor.constraint(equalTo: view.topAnchor, constant: 18),
            stack.bottomAnchor.constraint(equalTo: view.bottomAnchor, constant: -18),
            help.widthAnchor.constraint(equalTo: stack.widthAnchor),
            actions.widthAnchor.constraint(equalTo: stack.widthAnchor),
            scroll.widthAnchor.constraint(equalTo: stack.widthAnchor),
            scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 180)
        ])
        reload()
    }

    @objc private func reload() {
        generation += 1; let current = generation
        let directory = ActivityAuditStore.directory(for: profile)
        status.stringValue = "Reading local audit…"
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let result = Result { try ActivityAuditStore.read(directory: directory) }
            DispatchQueue.main.async {
                guard let self, self.generation == current else { return }
                switch result {
                case .success(let items): self.rows = items; self.filter()
                case .failure(let error): self.status.stringValue = "Audit unavailable: " + error.localizedDescription
                }
            }
        }
    }

    private func filter() {
        let q = search.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        visible = rows.filter { item in q.isEmpty || [item.tool,item.workspace ?? "",item.client ?? "shared",item.result].contains { $0.localizedCaseInsensitiveContains(q) } }
        table.reloadData(); export.isEnabled = !visible.isEmpty
        status.stringValue = "\(visible.count) of \(rows.count) events"
    }
    func controlTextDidChange(_ obj: Notification) { filter() }
    func numberOfRows(in tableView: NSTableView) -> Int { visible.count }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard visible.indices.contains(row), let col = tableColumn else { return nil }
        let event = visible[row]
        let text: String
        switch col.identifier.rawValue {
        case "time": text = formatter.string(from: event.time)
        case "tool": text = event.tool
        case "scope": text = event.workspace ?? "—"
        case "client": text = event.client ?? "Shared / local"
        case "result": text = event.result
        case "duration": text = String(event.durationMs)
        default: text = String(event.responseBytes)
        }
        let label = NSTextField(labelWithString: text); label.toolTip = text; label.lineBreakMode = .byTruncatingTail
        return label
    }
    @objc private func exportRows() {
        let panel = NSSavePanel(); panel.nameFieldStringValue = "activity-audit.json"
        panel.message = "Export the filtered local metadata; verified client IDs may identify your connections."
        guard !visible.isEmpty, panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted,.sortedKeys]; encoder.dateEncodingStrategy = .iso8601
            try encoder.encode(visible).write(to: url, options: .atomic)
            status.stringValue = "Exported \(visible.count) events."
        } catch { status.stringValue = "Export failed: " + error.localizedDescription }
    }
}
