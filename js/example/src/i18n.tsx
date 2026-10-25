import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { en, zh, type Copy } from "./copy";

export type Lang = "zh" | "en";

const LANG_KEY = "papersharp.lang";
const dictionaries: Record<Lang, Copy> = { zh, en };

const LangContext = createContext<{ lang: Lang; setLang: (l: Lang) => void; copy: Copy }>({
  lang: "zh",
  setLang: () => {},
  copy: zh,
});

function initialLang(): Lang {
  const saved = typeof localStorage !== "undefined" ? localStorage.getItem(LANG_KEY) : null;
  if (saved === "zh" || saved === "en") return saved;
  return typeof navigator !== "undefined" && navigator.language.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function LangProvider({ children }: { children: ReactNode }) {
  const [lang, setLang] = useState<Lang>(initialLang);

  useEffect(() => {
    localStorage.setItem(LANG_KEY, lang);
    document.documentElement.lang = lang === "zh" ? "zh-CN" : "en";
  }, [lang]);

  const value = useMemo(() => ({ lang, setLang, copy: dictionaries[lang] }), [lang]);
  return <LangContext.Provider value={value}>{children}</LangContext.Provider>;
}

export function useCopy() {
  return useContext(LangContext);
}
