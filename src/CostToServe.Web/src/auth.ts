import { InteractionRequiredAuthError, PublicClientApplication, type AccountInfo, type IPublicClientApplication } from "@azure/msal-browser";
import type { Persona } from "./types";

export interface AuthConfig {
  mode: "entra" | "devPersonas" | "none";
  tenantId?: string;
  clientId?: string;
  scopes?: string[];
}

/** Supplies request headers that identify the caller to the API. */
export interface ApiAuth {
  headers(): Promise<Record<string, string>>;
}

export async function loadAuthConfig(): Promise<AuthConfig> {
  const response = await fetch("/api/auth-config");
  if (!response.ok) {
    throw new Error(`Could not load sign-in settings (${response.status}).`);
  }
  return (await response.json()) as AuthConfig;
}

/** Local development only: the API accepts this header when Entra is not configured. */
export const personaAuth = (persona: Persona): ApiAuth => ({
  headers: async () => ({ "X-Demo-Persona": persona }),
});

/** Entra sign-in with MSAL (authorization code flow with PKCE; no client secret). */
export class EntraAuth implements ApiAuth {
  private constructor(
    private readonly msal: IPublicClientApplication,
    private readonly scopes: string[],
  ) {}

  static async create(config: AuthConfig): Promise<EntraAuth> {
    const msal = await PublicClientApplication.createPublicClientApplication({
      auth: {
        clientId: config.clientId!,
        authority: `https://login.microsoftonline.com/${config.tenantId}`,
        redirectUri: window.location.origin,
        postLogoutRedirectUri: window.location.origin,
      },
      // Tokens stay in this browser tab's session only.
      cache: { cacheLocation: "sessionStorage" },
    });
    const result = await msal.handleRedirectPromise();
    const account = result?.account ?? msal.getAllAccounts()[0];
    if (account) {
      msal.setActiveAccount(account);
    }
    return new EntraAuth(msal, config.scopes ?? []);
  }

  get account(): AccountInfo | null {
    return this.msal.getActiveAccount();
  }

  signIn() {
    return this.msal.loginRedirect({ scopes: this.scopes, prompt: "select_account" });
  }

  signOut() {
    return this.msal.logoutRedirect({ account: this.account ?? undefined });
  }

  async headers(): Promise<Record<string, string>> {
    try {
      const token = await this.msal.acquireTokenSilent({ scopes: this.scopes, account: this.account ?? undefined });
      return { Authorization: `Bearer ${token.accessToken}` };
    } catch (error) {
      if (error instanceof InteractionRequiredAuthError) {
        await this.msal.acquireTokenRedirect({ scopes: this.scopes });
      }
      throw error;
    }
  }
}
