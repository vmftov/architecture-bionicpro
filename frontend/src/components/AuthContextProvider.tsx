import { ReactNode, useCallback, useEffect, useState } from "react";
import { AuthContext } from "../contexts/AuthContext";

const pollingTimeout = 2000;

export interface AuthContextProviderProps {
    apiUri: string;
    children: ReactNode;
}

export function AuthContextProvider({ apiUri, children }: AuthContextProviderProps) {
    const [initialized, setInitialized] = useState<boolean>(false);
    const [authenticated, setAuthenticated] = useState<boolean>(false);

    useEffect(() => {
        var live = true;

        async function checkInitialized() {
            if (!live) {
                return true;
            }
            const response = await fetch(`${apiUri}/status`);
            if (response.ok) {
                setInitialized(true);
                return true;
            }
            return false;
        }

        async function checkAuthenticated() {
            if (!live) {
                return true;
            }
            const response = await fetch(`${apiUri}/auth/status`, { credentials: "include" });
            if (response.ok) {
                const json = (await response.json()) as { authenticated: boolean };
                if (json.authenticated) {
                    setAuthenticated(true);
                    return true;
                }
            }
            return false;
        }

        if (!initialized) {
            invokeRepeatedly(() => checkInitialized());
        } else if (!authenticated) {
            invokeRepeatedly(() => checkAuthenticated());
        }

        return () => {
            live = false;
        };
    }, [apiUri, authenticated, initialized]);

    const login = useCallback(async () => {
        window.location.href = `${apiUri}/login`;
    }, [apiUri]);

    return <AuthContext.Provider value={{ initialized, authenticated, login }}>{children}</AuthContext.Provider>;
}

async function invokeRepeatedly(action: () => Promise<boolean>) {
    let done: boolean;
    try {
        done = await action();
    } catch (err) {
        done = false;
    }
    if (!done) {
        setTimeout(() => invokeRepeatedly(action), pollingTimeout);
    }
}
