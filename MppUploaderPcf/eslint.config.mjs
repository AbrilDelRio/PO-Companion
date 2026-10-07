import microsoftPowerApps from "@microsoft/eslint-plugin-power-apps";
import globals from "globals";
import tseslint from "typescript-eslint";

export default tseslint.config(
    ...tseslint.configs.recommendedTypeChecked,
    {
        files: ["**/*.ts"],
        languageOptions: {
            globals: {
                ...globals.browser
            },
            parserOptions: {
                projectService: true,
                tsconfigRootDir: import.meta.dirname
            }
        },
        plugins: {
            "@microsoft/power-apps": microsoftPowerApps
        },
        rules: {
            ...microsoftPowerApps.configs.paCheckerHosted.rules
        },
        settings: {
            ...microsoftPowerApps.configs.paCheckerHosted.settings
        }
    },
    {
        ignores: ["out/**", "node_modules/**", "**/generated/**"]
    }
);
