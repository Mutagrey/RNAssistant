(function () {
  "use strict";
  var P = window.Prototype, tree = window.RNAssistantNavigationTree;
  var mounts = new Map(), serial = 0;

  function mount(root) {
    if (mounts.has(root)) return;
    var workspace = root.dataset.productTree === "workspace";
    var section = P.activeSection();
    var key = workspace ? "workspace:" + P.state.workspaceScope : section;
    var source = workspace ? P.workspaceNodes() : P.sectionTreeNodes();
    root.id = root.id || "navigation-tree-" + (++serial);
    function normalize(nodes) {
      return nodes.map(function (node) {
        var copy = Object.assign({}, node);
        copy.kind = node.kind === "item" || node.kind === "chat" ? "leaf" : node.kind;
        copy.selectable = !!node.route;
        if (node.children) copy.children = normalize(node.children);
        return copy;
      });
    }
    tree.render(root, normalize(source), {
      label: root.getAttribute("aria-label") || "Дерево раздела",
      positionKey: key,
      onSelect: function (node) { P.activateTreeItem(node); },
      onToggle: function (node, expanded) {
        if (workspace) P.state.collapsedParents[node.groupKey] = !expanded;
        else P.state.navCollapsed[node.groupKey] = !expanded;
      }
    });
    mounts.set(root, key);
  }

  P.productTree = {
    mount: function (scope) {
      (scope || document).querySelectorAll("[data-product-tree]").forEach(function (root) {
        var dialog = root.closest("dialog");
        if (!dialog || dialog.open) mount(root);
      });
    },
    unmount: function (scope) {
      mounts.forEach(function (key, root) {
        if (scope && !scope.contains(root)) return;
        tree.unmount(root, key);
        mounts.delete(root);
      });
    }
  };
}());
