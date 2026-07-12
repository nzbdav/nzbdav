import eslint from "@eslint/js";
import reactHooks from "eslint-plugin-react-hooks";
import tseslint from "typescript-eslint";

export default tseslint.config(
  {
    ignores: [
      "build/**",
      "dist-node/**",
      ".react-router/**",
      "node_modules/**",
      "coverage/**",
      // Build/compile outputs that may exist locally but are gitignored.
      "server.js",
      "server.d.ts",
      "vite.config.js",
      "vite.config.d.ts",
    ],
  },
  eslint.configs.recommended,
  ...tseslint.configs.recommended,
  {
    plugins: {
      "react-hooks": reactHooks,
    },
    languageOptions: {
      globals: {
        console: "readonly",
        process: "readonly",
        Buffer: "readonly",
        __dirname: "readonly",
        __filename: "readonly",
        setTimeout: "readonly",
        clearTimeout: "readonly",
        setInterval: "readonly",
        clearInterval: "readonly",
        fetch: "readonly",
        FormData: "readonly",
        File: "readonly",
        Blob: "readonly",
        URL: "readonly",
        URLSearchParams: "readonly",
        AbortController: "readonly",
        Request: "readonly",
        Response: "readonly",
        Headers: "readonly",
        WebSocket: "readonly",
        MessageEvent: "readonly",
        NodeJS: "readonly",
        document: "readonly",
        window: "readonly",
        localStorage: "readonly",
        sessionStorage: "readonly",
        HTMLElement: "readonly",
        customElements: "readonly",
        React: "readonly",
      },
    },
    rules: {
      // Enforce immediately — backlog cleared in this PR.
      "no-var": "error",

      // Plugin registered so existing eslint-disable comments resolve.
      // Full react-hooks recommended (incl. React Compiler rules) is a follow-up ratchet.
      "react-hooks/rules-of-hooks": "warn",
      "react-hooks/exhaustive-deps": "warn",

      // Existing backlog — ratchet to error in a follow-up.
      "@typescript-eslint/no-explicit-any": "warn",
      "@typescript-eslint/no-unused-vars": [
        "warn",
        {
          argsIgnorePattern: "^_",
          varsIgnorePattern: "^_",
        },
      ],
      "@typescript-eslint/no-empty-object-type": "warn",
      "@typescript-eslint/no-unused-expressions": "warn",
      "no-unused-expressions": "off",
      "no-empty": "warn",
      "no-extra-boolean-cast": "warn",
      "no-useless-assignment": "warn",
      "prefer-const": "warn",
      "no-undef": "warn",
    },
  },
);
