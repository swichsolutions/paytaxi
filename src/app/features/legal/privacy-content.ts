import type { Block, LegalDoc, LegalLang } from './legal-doc';

/**
 * The privacy policy text, one document per language. Section ids are shared by all three
 * languages so a link like /privacy/en#rights points at the same section in each.
 *
 * Lean by design: every item the Law of Georgia on Personal Data Protection (Arts. 24–25)
 * requires is said once — the controller (each taxi park; the driver finds its legal name, ID code
 * and phone in the app under Profile → Your park, fed by /api/driver/me), the processors ({provider}
 * and {operator}), which data and its source, purposes and legal basis, whether the data is
 * required, legitimate interests, recipients, transfers abroad and their basis, retention, the
 * Chapter III rights with their deadlines and the complaint route (the State Audit Office, since the
 * Personal Data Protection Service was liquidated on 2 March 2026). One table in section 2 carries
 * data, source, purpose/basis and retention; do not add a second summary of it. The terms of use
 * cover the service itself; do not repeat them here. Account data is kept until the park
 * suspends the driver's PayTaxi account because the driver stopped driving for it or asked for it — the
 * system's only "switched off" driver state (DriverStatus.Suspended; the console says "suspend")
 * and the word the terms use (ka „შეჩერება“, ru «приостановить»); there is no "closed" state.
 *
 * Deletion promises match the backend's RetentionWorker exactly: sign-in codes deleted within a day
 * (OtpCodes older than 24 h are purged on every poll, not only in the nightly pass), device sessions after 90 days, the call log (ApiAuditLogs) and sign-in history after 2 years.
 * Removed payout accounts are NOT deleted (cashouts reference them): they stay with the financial
 * records. Do not promise any other automatic deletion here until the code does it.
 *
 * Security claims stay modest: phone numbers and IBANs are encrypted (the phone's lookup hash is a
 * keyed HMAC), passwords (BCrypt), sign-in codes (keyed HMAC) and session tokens (SHA-256 of a random
 * token) are stored only as hashes. Do not call them "irreversible" or claim more than that.
 *
 * The Russian provider name ends in a parenthetical („ООО «…» (… LLC)“): never put „(…)“ straight
 * after {provider} in the Russian text.
 *
 * Every statement here must stay true to what the system actually does — when the code
 * changes what is collected, kept or shared, change this text in all three languages and
 * bump PRIVACY_META.version. {placeholders} are filled from LEGAL_DETAILS (legal-config.ts);
 * keep them in the nominative (no case ending attached) and never put one in the first column
 * of a table (row headers are rendered as plain text). Units travel with the value
 * ({financialYears}, {logDays}), so no number in the text is followed by a noun.
 *
 * Georgian is the governing text; English and Russian are translations. Georgian legal terms
 * follow the law: controller = „დამუშავებისთვის პასუხისმგებელი პირი“, processor =
 * „დამუშავებაზე უფლებამოსილი პირი“, data subject = „მონაცემთა სუბიექტი“, legitimate interest =
 * „ლეგიტიმური ინტერესი“ (Art. 5(1)(ი)), profiling = „პროფაილინგი“. In the Georgian text
 * „PayTaxi-ს ანგარიში“ is the driver's account in the app and plain „ანგარიში“ a bank account.
 */
export type PrivacyDoc = LegalDoc;

const p = (text: string): Block => ({ kind: 'p', text });
const ul = (...items: string[]): Block => ({ kind: 'ul', items });
const table = (head: string[], ...rows: string[][]): Block => ({ kind: 'table', head, rows });

// ═══════════════════════════════════════════════════════════════════════
//  English
// ═══════════════════════════════════════════════════════════════════════
const en: PrivacyDoc = {
  lang: 'en',
  langName: 'English',
  eyebrow: 'Legal',
  title: 'Privacy Policy',
  subtitle: 'How PayTaxi handles the personal data of drivers and admin-console users.',
  versionLabel: 'Version',
  effectiveLabel: 'In force from',
  draftDateLabel: 'Draft of',
  draftTag: 'Draft',
  draftBanner: 'Draft — under legal review and not yet in force. Highlighted items are still to be completed.',
  blankTitle: 'To be completed',
  blanks: {
    operator: 'legal name of the operator',
    operatorId: 'operator’s identification code',
    operatorAddress: 'operator’s registered address',
    provider: 'legal name of the platform provider',
    providerId: 'identification code',
    providerAddress: 'registered address',
    email: 'privacy contact e-mail',
    support: 'PayTaxi support contact',
    sms: 'SMS provider',
    hosting: 'hosting provider',
    hostingLocation: 'data-centre country',
    yandexCountry: 'country of the Yandex company',
    transferBasis: 'legal basis and safeguards of each transfer',
    financialYears: 'N years',
    logDays: 'N days',
  },
  toc: 'Contents',
  skipToContent: 'Skip to the policy',
  print: 'Print',
  backToApp: 'Back to PayTaxi',
  governingNote: 'This is a translation. If it differs from the Georgian text, the Georgian text applies.',
  summaryTitle: 'In short',
  summary: [
    'Your taxi park is responsible for your personal data; {provider} runs PayTaxi for the park.',
    'To see, correct or delete your data, contact your park or {email}.',
  ],
  sections: [
    {
      id: 'who-we-are',
      title: 'Who is responsible',
      blocks: [
        p('Under the Law of Georgia on Personal Data Protection, your taxi park is the controller of your personal data: it decides why and how the data is processed. Its legal name, identification code and phone number are in the app under Profile → Your park.'),
        p('{provider} (identification code {providerId}) owns and runs the platform; {operator} (identification code {operatorId}, {operatorAddress}) operates it in Georgia under licence, registering and supporting drivers ({provider} does so for parks it connected directly). Both are processors acting only on your park’s instructions, except that they keep your sign-in history, proof that you accepted the Terms of Use, on their own account.'),
        p('“PayTaxi” and “we” mean these two companies. Yandex and the banks are independent controllers.'),
      ],
    },
    {
      id: 'data',
      title: 'Your data: what, from where, why and how long',
      blocks: [
        p('“Contract” means your arrangement with your park for paying out your earnings, with the PayTaxi Terms of Use. A legitimate interest is your park’s unless another is named.'),
        table(['Data', 'Source', 'Purpose and legal basis', 'How long'],
          ['Phone number, name, Yandex driver ID', 'Your park (directly or from its Yandex driver list)', 'Signing in, linking you to your Yandex earnings, own-name check of payout accounts (contract); invoices (legal obligation)', 'Until your park suspends your PayTaxi account when you stop driving for it or at your request; then only what the financial records need'],
          ['Yandex data: balance, transactions, rides (time, fare, pickup and drop-off addresses), car plate, driver-list entry', 'Yandex, via your park’s Fleet access', 'Showing your earnings to you and the driver list to your park (contract); checking cashouts (legitimate interest)', 'Not stored (up to 60 seconds in memory), except the latest balance'],
          ['Payout accounts: IBAN, bank, holder’s name', 'You (own name only), or your park or PayTaxi for it', 'Paying your cashouts (contract); invoices and accounting records (legal obligation)', 'While in use; after removal, kept with the cashout records: {financialYears}'],
          ['Cashouts: amount, fee, time, status, bank and Yandex references, invoice number; if cancelled, why and by whom', 'Each cashout; the bank and Yandex', 'Paying you and debiting your Yandex balance (contract); invoices and accounting (legal obligation); reconciliation and your park’s fee settlement with PayTaxi (legitimate interest)', '{financialYears}'],
          ['Sign-in codes; signed-in devices and sign-in history (e.g. “Android · Chrome”, sign-in and last-use times)', 'Your device', 'Signing in (contract); sign-in history (PayTaxi’s legitimate interest)', 'Codes: valid 5 minutes, deleted within a day; devices: up to 90 days; sign-in history: 2 years'],
          ['In-app messages about your cashouts', 'PayTaxi', 'Cashout updates (contract)', 'As for the phone number'],
          ['IP address; technical logs; log of calls to Yandex and the banks', 'Your device; PayTaxi', 'Security, fraud prevention, fixing faults, disputes, Yandex’s audits of API use (legitimate interest)', 'IP address: used to limit sign-in attempts, not stored in our database; technical logs (may include IP address, ID and name): {logDays} at the hosting provider; call log: 2 years'],
          ['Admin-console users (park, operator and platform staff): e-mail, name, role, park, password hash, last sign-in, failed attempts, money and settings actions', 'Their employer (the controller)', 'Secure access and accountability (the employer’s legitimate interest)', 'While access lasts; action records as long as the record concerned'],
        ),
        p('At your request your park may add an account in another person’s name, recording why; that person’s name and IBAN are processed to pay you (legitimate interest), and your park must give them this policy.'),
        p('Your phone number and an IBAN are required by the service, not by law: without them you cannot sign in or cash out. No processing here relies on consent.'),
        p('Expired data is deleted or kept in depersonalised form. If your park leaves PayTaxi, its drivers’ data is returned to it and deleted within 30 days, except what the law requires to be kept.'),
      ],
    },
    {
      id: 'we-do-not',
      title: 'Profiling, automatic checks and cookies',
      blocks: [
        ul(
          'No selling, advertising, marketing or profiling.',
          'Automatic checks refuse a cashout above your Yandex balance or your park’s limits or during a suspension, and a new payout account not in your name or at a bank your park does not pay to. The app says why; you can give your view, contest it and have a person review it.',
          'No cookies or trackers. Your browser’s local storage keeps only your sign-in data, language and (admin console) last park chosen.',
        ),
      ],
    },
    {
      id: 'recipients',
      title: 'Who receives your data',
      blocks: [
        p('Besides your park (which sees only its own drivers) and PayTaxi:'),
        ul(
          'Yandex: each cashout, as a debit on your Yandex balance.',
          'Your park’s bank (currently TBC Bank): the receiving IBAN, holder’s name, amount and payment description.',
          'SMS provider ({sms}): your phone number, your sign-in code and the text of notices about your payout accounts.',
          'Hosting provider ({hosting}): stores PayTaxi’s data and technical logs.',
          'Google: your IP address when the page loads its font from Google, and e-mails to {email} (a Google mailbox).',
          'Public authorities and courts: only where Georgian law requires.',
        ),
      ],
    },
    {
      id: 'transfers',
      title: 'Transfers outside Georgia',
      blocks: [
        p('Yandex ({yandexCountry}), Google (USA) and, if its data centre ({hostingLocation}) is abroad, our hosting provider receive data outside Georgia. Legal basis and safeguards of each transfer: {transferBasis}; ask {email} for a copy.'),
      ],
    },
    {
      id: 'security',
      title: 'How we protect it',
      blocks: [
        ul(
          'Connections, stored phone numbers and IBANs, and the parks’ bank and Yandex keys are encrypted; passwords, sign-in codes and session tokens are stored only as hashes.',
          'Sign-in attempts are limited and admin accounts lock after repeated wrong passwords; staff see only what their role needs; every money movement is logged.',
          'If a breach may affect you, we tell your park without delay so that it can notify the State Audit Office of Georgia and, where required, you.',
        ),
      ],
    },
    {
      id: 'rights',
      title: 'Your rights',
      blocks: [
        p('You have the right to:'),
        ul(
          'know whether and how your data is processed, and get a copy;',
          'have it corrected, updated or completed;',
          'have processing stopped, or your data deleted, destroyed or blocked, as the law provides;',
          'receive data you provided in machine-readable form or have it sent to another controller, as the law provides;',
          'not be subject to solely automated decisions with significant effects, except as the law allows;',
          'complain to the State Audit Office of Georgia (sao.ge) or a court.',
        ),
        p('We pass requests to your park and may first verify your identity. Answers are free, within 10 working days (for information and copies, exceptionally up to 10 more, with the reason); decisions on blocking within 3.'),
      ],
    },
    {
      id: 'contact',
      title: 'Changes, age and contact',
      blocks: [
        p('Changes update the version and date at the top; significant ones are announced in the app before they apply. PayTaxi is for adults (18+) only.'),
        p('Contact: your park, {email}, or by post {provider}, {providerAddress}. PayTaxi has no personal data protection officer; if your park has one, it will tell you.'),
      ],
    },
  ],
  footer: 'PayTaxi is provided by {provider}.',
};

// ═══════════════════════════════════════════════════════════════════════
//  ქართული (governing text)
// ═══════════════════════════════════════════════════════════════════════
const ka: PrivacyDoc = {
  lang: 'ka',
  langName: 'ქართული',
  eyebrow: 'იურიდიული ინფორმაცია',
  title: 'კონფიდენციალურობის პოლიტიკა',
  subtitle: 'როგორ ამუშავებს PayTaxi მძღოლებისა და ადმინ კონსოლის მომხმარებლების პერსონალურ მონაცემებს.',
  versionLabel: 'ვერსია',
  effectiveLabel: 'ძალაშია',
  draftDateLabel: 'პროექტი,',
  draftTag: 'პროექტი',
  draftBanner: 'პროექტი — მიმდინარეობს იურიდიული შემოწმება, ჯერ ძალაში არ არის. მონიშნული ველები შესავსებია.',
  blankTitle: 'შესავსებია',
  blanks: {
    operator: 'ოპერატორის იურიდიული სახელწოდება',
    operatorId: 'ოპერატორის საიდენტიფიკაციო კოდი',
    operatorAddress: 'ოპერატორის იურიდიული მისამართი',
    provider: 'პლატფორმის მომწოდებლის სახელწოდება',
    providerId: 'საიდენტიფიკაციო კოდი',
    providerAddress: 'იურიდიული მისამართი',
    email: 'ელფოსტა მონაცემთა საკითხებზე',
    support: 'PayTaxi-ს მხარდაჭერის კონტაქტი',
    sms: 'SMS მომწოდებელი',
    hosting: 'ჰოსტინგის მომწოდებელი',
    hostingLocation: 'მონაცემთა ცენტრის ქვეყანა',
    yandexCountry: 'Yandex-ის კომპანიის ქვეყანა',
    transferBasis: 'თითოეული გადაცემის სამართლებრივი საფუძველი და გარანტიები',
    financialYears: 'N წელი',
    logDays: 'N დღე',
  },
  toc: 'სარჩევი',
  skipToContent: 'პოლიტიკის ტექსტზე გადასვლა',
  print: 'ბეჭდვა',
  backToApp: 'PayTaxi-ში დაბრუნება',
  governingNote: '',
  summaryTitle: 'მოკლედ',
  summary: [
    'თქვენს პერსონალურ მონაცემებზე პასუხისმგებელია თქვენი ტაქსოპარკი; PayTaxi-ს მუშაობას პარკისთვის უზრუნველყოფს {provider}.',
    'მონაცემების სანახავად, გასასწორებლად ან წასაშლელად მიმართეთ თქვენს პარკს ან მოგვწერეთ: {email}.',
  ],
  sections: [
    {
      id: 'who-we-are',
      title: 'ვინ არის პასუხისმგებელი',
      blocks: [
        p('საქართველოს კანონის „პერსონალურ მონაცემთა დაცვის შესახებ“ თანახმად, თქვენი პერსონალური მონაცემების დამუშავებისთვის პასუხისმგებელი პირია თქვენი ტაქსოპარკი: ის განსაზღვრავს, რა მიზნით და როგორ მუშავდება მონაცემები. პარკის იურიდიული სახელწოდება, საიდენტიფიკაციო კოდი და ტელეფონის ნომერი მითითებულია აპლიკაციაში: „პროფილი“ → „თქვენი პარკი“.'),
        p('{provider} (საიდენტიფიკაციო კოდი {providerId}) პლატფორმის მფლობელია და უზრუნველყოფს მის მუშაობას; {operator} (საიდენტიფიკაციო კოდი {operatorId}, {operatorAddress}) საქართველოში PayTaxi-ს ოპერირებას ლიცენზიით ახორციელებს: არეგისტრირებს მძღოლებს და უწევს მათ მხარდაჭერას (პარკებში, რომლებიც პლატფორმის მფლობელმა უშუალოდ დააკავშირა, ამას {provider} აკეთებს). ორივე დამუშავებაზე უფლებამოსილი პირია და მოქმედებს მხოლოდ თქვენი პარკის მითითებით, გარდა თქვენი შესვლის ისტორიისა, რომელსაც საკუთარი სახელით ინახავენ, როგორც სარგებლობის პირობების თქვენ მიერ მიღების დადასტურებას.'),
        p('ამ პოლიტიკაში „PayTaxi“ და „ჩვენ“ ნიშნავს ამ ორ კომპანიას. Yandex და ბანკები დამოუკიდებელი დამუშავებისთვის პასუხისმგებელი პირები არიან.'),
      ],
    },
    {
      id: 'data',
      title: 'თქვენი მონაცემები: რა, საიდან, რისთვის და რამდენ ხანს',
      blocks: [
        p('„ხელშეკრულება“ ქვემოთ ნიშნავს პარკთან თქვენს შეთანხმებას გამომუშავებული თანხის გაცემის შესახებ, PayTaxi-ს სარგებლობის პირობებთან ერთად. ლეგიტიმური ინტერესი ქვემოთ თქვენი პარკის ინტერესია, თუ სხვა არ არის მითითებული.'),
        table(['მონაცემები', 'წყარო', 'მიზანი და სამართლებრივი საფუძველი', 'შენახვის ვადა'],
          ['ტელეფონის ნომერი, სახელი, Yandex-ის მძღოლის იდენტიფიკატორი', 'თქვენი პარკი (უშუალოდ ან მისი Yandex-ის მძღოლების სიიდან)', 'სისტემაში შესვლა, თქვენი Yandex-ის შემოსავალთან დაკავშირება, იმის შემოწმება, რომ გამოტანის ანგარიშები თქვენს სახელზეა (ხელშეკრულება); ინვოისები (კანონისმიერი მოვალეობა)', 'სანამ პარკი არ შეაჩერებს თქვენს PayTaxi-ს ანგარიშს, როცა მასთან თანამშრომლობას შეწყვეტთ, ან თქვენი მოთხოვნით; შემდეგ — მხოლოდ ის, რაც ფინანსურ ჩანაწერებს სჭირდება'],
          ['Yandex-ის მონაცემები: ბალანსი, ტრანზაქციები, მგზავრობები (დრო, ღირებულება, აყვანისა და ჩამოსვლის მისამართები), ავტომობილის სახელმწიფო ნომერი, თქვენი ჩანაწერი პარკის მძღოლების სიაში', 'Yandex — თქვენი პარკის Yandex Fleet-ზე წვდომით', 'შემოსავლის ჩვენება თქვენთვის და მძღოლების სიის ჩვენება პარკისთვის (ხელშეკრულება); გამოტანების შემოწმება (ლეგიტიმური ინტერესი)', 'არ ინახება (მეხსიერებაში რჩება 60 წამამდე), გარდა ბოლო ბალანსისა'],
          ['გამოტანის ანგარიშები: IBAN, ბანკი, მფლობელის სახელი', 'თქვენ (მხოლოდ საკუთარ სახელზე გახსნილი), ან პარკი ან მისი სახელით PayTaxi', 'გამოტანების ჩარიცხვა (ხელშეკრულება); ინვოისები და საბუღალტრო აღრიცხვა (კანონისმიერი მოვალეობა)', 'სანამ იყენებთ; პროფილიდან წაშლის შემდეგ ინახება გამოტანების ჩანაწერებთან ერთად — {financialYears}'],
          ['გამოტანები: თანხა, საკომისიო, დრო, სტატუსი, ბანკისა და Yandex-ის ოპერაციების ნომრები, ინვოისის ნომერი; გაუქმებისას — მიზეზი და ვინ გააუქმა', 'ყოველი გამოტანა; ბანკი და Yandex', 'ჩარიცხვა და Yandex-ის ბალანსიდან ჩამოწერა (ხელშეკრულება); ინვოისები და საბუღალტრო აღრიცხვა (კანონისმიერი მოვალეობა); ბანკისა და Yandex-ის მონაცემებთან შედარება და პარკსა და PayTaxi-ს შორის საკომისიოს ანგარიშსწორება (ლეგიტიმური ინტერესი)', '{financialYears}'],
          ['შესვლის კოდები; მოწყობილობები, საიდანაც შესული ხართ, და შესვლის ისტორია (მაგ. „Android · Chrome“, შესვლისა და ბოლო გამოყენების დრო)', 'თქვენი მოწყობილობა', 'სისტემაში შესვლა (ხელშეკრულება); შესვლის ისტორია (PayTaxi-ს ლეგიტიმური ინტერესი)', 'კოდი: მოქმედებს 5 წუთი, იშლება ერთ დღეში; მოწყობილობა: 90 დღემდე; შესვლის ისტორია: 2 წელი'],
          ['შეტყობინებები აპლიკაციაში თქვენი გამოტანების შესახებ', 'PayTaxi', 'ინფორმაცია გამოტანის მიმდინარეობის შესახებ (ხელშეკრულება)', 'ისევე, როგორც ტელეფონის ნომერი'],
          ['IP მისამართი; ტექნიკური ჟურნალები; Yandex-თან და ბანკებთან მიმართვების ჟურნალი', 'თქვენი მოწყობილობა; PayTaxi', 'უსაფრთხოება, თაღლითობის თავიდან აცილება, ხარვეზების გამოსწორება, დავები, Yandex-ის მიერ API-ს გამოყენების შემოწმება (ლეგიტიმური ინტერესი)', 'IP მისამართი გამოიყენება შესვლის მცდელობების შესაზღუდად და ჩვენს მონაცემთა ბაზაში არ ინახება; ტექნიკური ჟურნალები (შეიძლება შეიცავდეს IP მისამართს, იდენტიფიკატორსა და სახელს) ჰოსტინგის მომწოდებელთან — {logDays}; მიმართვების ჟურნალი — 2 წელი'],
          ['ადმინ კონსოლის მომხმარებლები (პარკის, ოპერატორისა და პლატფორმის თანამშრომლები): ელფოსტა, სახელი, როლი, პარკი, პაროლის ჰეში, ბოლო შესვლა, წარუმატებელი მცდელობები, შესრულებული ფინანსური ოპერაციები და პარამეტრების ცვლილებები', 'დამსაქმებელი (დამუშავებისთვის პასუხისმგებელი პირი)', 'უსაფრთხო წვდომა და ქმედებების აღრიცხვა (დამსაქმებლის ლეგიტიმური ინტერესი)', 'სანამ წვდომა აქვს; ქმედების ჩანაწერი — იმ ჩანაწერის ვადით, რომელსაც ეხება'],
        ),
        p('თქვენი მოთხოვნით პარკს შეუძლია დაამატოს სხვა პირის სახელზე გახსნილი ანგარიში, მიზეზის აღრიცხვით. ამ შემთხვევაში ამ პირის სახელი და IBAN მუშავდება თქვენთვის თანხის ჩასარიცხად (ლეგიტიმური ინტერესი), პარკი კი ვალდებულია, მას ეს პოლიტიკა გააცნოს.'),
        p('ტელეფონის ნომერი და IBAN მომსახურების პირობაა და არა კანონისმიერი მოთხოვნა: მათ გარეშე სისტემაში ვერ შეხვალთ და თანხას ვერ გამოიტანთ. ზემოთ აღწერილი არცერთი დამუშავება თანხმობას არ ეფუძნება.'),
        p('ვადაგასული მონაცემები იშლება ან ინახება დეპერსონალიზებული ფორმით. თუ თქვენი პარკი PayTaxi-თ სარგებლობას შეწყვეტს, მისი მძღოლების მონაცემები 30 დღის განმავლობაში უბრუნდება პარკს და იშლება, გარდა იმისა, რისი შენახვაც კანონით არის სავალდებულო.'),
      ],
    },
    {
      id: 'we-do-not',
      title: 'პროფაილინგი, ავტომატური შემოწმებები და cookie-ები',
      blocks: [
        ul(
          'არ ვყიდით მონაცემებს, არ ვიყენებთ მათ რეკლამის ან მარკეტინგისთვის და არ ვახორციელებთ პროფაილინგს.',
          'ავტომატური შემოწმებები: გამოტანაზე უარი ითქმება, თუ თანხა აღემატება თქვენს Yandex-ის ბალანსს ან პარკის ლიმიტებს, ან თუ თქვენი PayTaxi-ს ანგარიში ან პარკი შეჩერებულია; გამოტანის ახალი ანგარიშის დამატებაზე — თუ ის თქვენს სახელზე არ არის ან ისეთ ბანკშია, რომელშიც პარკი თანხას არ რიცხავს. აპლიკაცია უარის მიზეზს გაჩვენებთ; შეგიძლიათ გამოთქვათ მოსაზრება, გაასაჩივროთ უარი და მოითხოვოთ, რომ საკითხი ადამიანმა განიხილოს.',
          'არ ვიყენებთ cookie-ებს და თვალთვალის ინსტრუმენტებს. ბრაუზერის ლოკალურ მეხსიერებაში ინახება მხოლოდ შესვლის მონაცემები, ენა და (ადმინ კონსოლში) ბოლოს არჩეული პარკი.',
        ),
      ],
    },
    {
      id: 'recipients',
      title: 'ვინ იღებს თქვენს მონაცემებს',
      blocks: [
        p('თქვენი პარკისა (ის მხოლოდ საკუთარ მძღოლებს ხედავს) და PayTaxi-ს გარდა:'),
        ul(
          'Yandex: ყოველი გამოტანა — თქვენი Yandex-ის ბალანსიდან ჩამოწერის სახით.',
          'თქვენი პარკის ბანკი (ამჟამად თიბისი ბანკი): მიმღების IBAN, მფლობელის სახელი, თანხა და გადახდის დანიშნულება.',
          'SMS მომწოდებელი ({sms}): თქვენი ტელეფონის ნომერი, შესვლის კოდი და თქვენი ანგარიშების შესახებ შეტყობინებების ტექსტი.',
          'ჰოსტინგის მომწოდებელი ({hosting}): ინახავს PayTaxi-ს მონაცემებსა და ტექნიკურ ჟურნალებს.',
          'Google: თქვენი IP მისამართი, როცა გვერდი შრიფტს Google-იდან ტვირთავს, და წერილები მისამართზე {email} (ეს საფოსტო ყუთი Google-ზეა).',
          'სახელმწიფო ორგანოები და სასამართლო: მხოლოდ საქართველოს კანონმდებლობით გათვალისწინებულ შემთხვევებში.',
        ),
      ],
    },
    {
      id: 'transfers',
      title: 'მონაცემების გადაცემა საქართველოს ფარგლებს გარეთ',
      blocks: [
        p('Yandex ({yandexCountry}) და Google (აშშ) მონაცემებს საქართველოს ფარგლებს გარეთ იღებენ, ისევე როგორც ჩვენი ჰოსტინგის მომწოდებელი, თუ მისი მონაცემთა ცენტრი ({hostingLocation}) საზღვარგარეთ მდებარეობს. თითოეული გადაცემის სამართლებრივი საფუძველი და გარანტიები: {transferBasis}; ასლის მოთხოვნა შეგიძლიათ მისამართზე {email}.'),
      ],
    },
    {
      id: 'security',
      title: 'როგორ ვიცავთ მონაცემებს',
      blocks: [
        ul(
          'კავშირი დაშიფრულია; ჩვენს სერვერებზე ტელეფონის ნომრები, IBAN-ები და პარკების საბანკო და Yandex-ის წვდომის გასაღებები დაშიფრულად ინახება, პაროლები, შესვლის კოდები და სესიის ტოკენები კი — მხოლოდ ჰეშის სახით.',
          'შესვლის მცდელობები შეზღუდულია, ადმინისტრატორის ანგარიში კი რამდენიმე არასწორი პაროლის შემდეგ იბლოკება; თანამშრომლები ხედავენ მხოლოდ იმას, რასაც მათი როლი მოითხოვს; ფულის ყოველი მოძრაობა აღირიცხება.',
          'თუ ინციდენტი შეიძლება თქვენზე აისახოს, დაუყოვნებლივ ვაცნობებთ თქვენს პარკს, რათა მან აცნობოს საქართველოს სახელმწიფო აუდიტის სამსახურს და, თუ კანონი მოითხოვს, თქვენც.',
        ),
      ],
    },
    {
      id: 'rights',
      title: 'თქვენი უფლებები',
      blocks: [
        p('როგორც მონაცემთა სუბიექტს, გაქვთ უფლება:'),
        ul(
          'მიიღოთ ინფორმაცია, მუშავდება თუ არა და როგორ მუშავდება თქვენი მონაცემები, და მიიღოთ მათი ასლი;',
          'მოითხოვოთ მათი გასწორება, განახლება ან შევსება;',
          'მოითხოვოთ დამუშავების შეწყვეტა, მონაცემების წაშლა, განადგურება ან დაბლოკვა — კანონით დადგენილ შემთხვევებში;',
          'მიიღოთ თქვენ მიერ მოწოდებული მონაცემები მანქანურად წაკითხვადი ფორმით ან მოითხოვოთ მათი გადაცემა სხვა დამუშავებისთვის პასუხისმგებელი პირისთვის — კანონით დადგენილ შემთხვევებში;',
          'არ დაექვემდებაროთ მხოლოდ ავტომატიზებულ გადაწყვეტილებას, რომელიც თქვენზე არსებითად მოქმედებს, გარდა კანონით დაშვებული შემთხვევებისა;',
          'საჩივრით მიმართოთ საქართველოს სახელმწიფო აუდიტის სამსახურს (sao.ge) ან სასამართლოს.',
        ),
        p('ჩვენთვის გამოგზავნილ მოთხოვნას პარკს გადავცემთ და შეიძლება ჯერ თქვენი ვინაობა დავადასტუროთ. პასუხი უფასოა და გეძლევათ არაუგვიანეს 10 სამუშაო დღისა (ინფორმაციისა და ასლის მოთხოვნისას გამონაკლის შემთხვევაში ვადა შეიძლება გაგრძელდეს არაუმეტეს 10 სამუშაო დღით, მიზეზის მითითებით); დაბლოკვის მოთხოვნაზე გადაწყვეტილებას გაცნობებთ არაუგვიანეს 3 სამუშაო დღისა.'),
      ],
    },
    {
      id: 'contact',
      title: 'ცვლილებები, ასაკი და კონტაქტი',
      blocks: [
        p('ყოველი ცვლილებისას გვერდის თავში ვერსია და თარიღი ახლდება; მნიშვნელოვან ცვლილებებს ძალაში შესვლამდე აპლიკაციაში გაცნობებთ. PayTaxi განკუთვნილია მხოლოდ სრულწლოვანი (18 წლის და უფროსი) პირებისთვის.'),
        p('კონტაქტი: თქვენი პარკი, ელფოსტა {email} ან საფოსტო მისამართი — {provider}, {providerAddress}. PayTaxi-ს პერსონალურ მონაცემთა დაცვის ოფიცერი არ ჰყავს; თუ ოფიცერი ჰყავს თქვენს პარკს, ამის შესახებ პარკი გაცნობებთ.'),
      ],
    },
  ],
  footer: 'PayTaxi-ს მომწოდებელია {provider}.',
};

// ═══════════════════════════════════════════════════════════════════════
//  Русский
// ═══════════════════════════════════════════════════════════════════════
const ru: PrivacyDoc = {
  lang: 'ru',
  langName: 'Русский',
  eyebrow: 'Правовая информация',
  title: 'Политика конфиденциальности',
  subtitle: 'Как PayTaxi обрабатывает персональные данные водителей и пользователей панели администратора.',
  versionLabel: 'Версия',
  effectiveLabel: 'Действует с',
  draftDateLabel: 'Проект от',
  draftTag: 'Проект',
  draftBanner: 'Проект — проходит юридическую проверку и ещё не действует. Выделенные поля нужно заполнить.',
  blankTitle: 'Нужно заполнить',
  blanks: {
    operator: 'наименование оператора',
    operatorId: 'идентификационный код оператора',
    operatorAddress: 'юридический адрес оператора',
    provider: 'наименование поставщика платформы',
    providerId: 'идентификационный код',
    providerAddress: 'юридический адрес',
    email: 'e-mail по вопросам данных',
    support: 'контакт поддержки PayTaxi',
    sms: 'SMS-провайдер',
    hosting: 'хостинг-провайдер',
    hostingLocation: 'страна дата-центра',
    yandexCountry: 'страна компании Yandex',
    transferBasis: 'правовое основание и гарантии каждой передачи',
    financialYears: 'N лет',
    logDays: 'N дней',
  },
  toc: 'Содержание',
  skipToContent: 'Перейти к тексту политики',
  print: 'Печать',
  backToApp: 'Вернуться в PayTaxi',
  governingNote: 'Это перевод. При расхождении с грузинским текстом применяется грузинский текст.',
  summaryTitle: 'Коротко',
  summary: [
    'За ваши персональные данные отвечает ваш таксопарк; {provider} обеспечивает для него работу PayTaxi.',
    'Чтобы посмотреть, исправить или удалить свои данные, обратитесь в парк или напишите на {email}.',
  ],
  sections: [
    {
      id: 'who-we-are',
      title: 'Кто отвечает за данные',
      blocks: [
        p('По Закону Грузии «О защите персональных данных» лицо, ответственное за обработку ваших персональных данных, — ваш таксопарк: он определяет, зачем и как они обрабатываются. Его юридическое наименование, идентификационный код и телефон указаны в приложении: «Профиль» → «Ваш парк».'),
        p('{provider}, идентификационный код {providerId}, владеет платформой и обеспечивает её работу; {operator} (идентификационный код {operatorId}, {operatorAddress}) ведёт работу PayTaxi в Грузии по лицензии: регистрирует водителей и оказывает им поддержку, а в парках, подключённых владельцем платформы напрямую, это делает {provider}. Обе компании — лица, уполномоченные на обработку, и действуют только по указаниям вашего парка, кроме истории ваших входов: её они хранят от своего имени как подтверждение того, что вы приняли Условия использования.'),
        p('В этой политике «PayTaxi» и «мы» — эти две компании. Yandex и банки — самостоятельные лица, ответственные за обработку.'),
      ],
    },
    {
      id: 'data',
      title: 'Ваши данные: что, откуда, зачем и как долго',
      blocks: [
        p('«Договор» ниже — ваша договорённость с парком о выплате заработка вместе с Условиями использования PayTaxi. Законный интерес ниже — интерес вашего парка, если не указано иное.'),
        table(['Данные', 'Источник', 'Цель и правовое основание', 'Срок хранения'],
          ['Номер телефона, имя, ID водителя в Yandex', 'Ваш парк (напрямую или из своего списка водителей в Yandex)', 'Вход, связь с вашим заработком в Yandex, проверка, что счета для выводов открыты на ваше имя (договор); инвойсы (обязанность по закону)', 'Пока парк не приостановит ваш аккаунт PayTaxi — когда вы перестаёте с ним сотрудничать или по вашей просьбе; затем — только то, что нужно для финансовых записей'],
          ['Данные Yandex: баланс, транзакции, поездки (время, стоимость, адреса подачи и высадки), номер автомобиля, запись в списке водителей парка', 'Yandex — через доступ вашего парка к Yandex Fleet', 'Показ заработка вам и списка водителей парку (договор); проверка выводов (законный интерес)', 'Не хранятся (в памяти до 60 секунд), кроме последнего баланса'],
          ['Счета для выводов: IBAN, банк, имя владельца', 'Вы (только счета на своё имя), либо парк или PayTaxi от его имени', 'Зачисление выводов (договор); инвойсы и бухгалтерский учёт (обязанность по закону)', 'Пока используются; после удаления из профиля хранятся вместе с записями о выводах — {financialYears}'],
          ['Выводы: сумма, комиссия, время, статус, номера операций в банке и Yandex, номер инвойса; при отмене — причина и кто отменил', 'Каждый вывод; банк и Yandex', 'Перевод и списание с баланса в Yandex (договор); инвойсы и бухгалтерский учёт (обязанность по закону); сверка с банком и Yandex и расчёт комиссии между парком и PayTaxi (законный интерес)', '{financialYears}'],
          ['Коды для входа; устройства, с которых выполнен вход, и история входов (например, «Android · Chrome», время входа и последнего использования)', 'Ваше устройство', 'Вход (договор); история входов (законный интерес PayTaxi)', 'Коды: действуют 5 минут, удаляются в течение суток; устройства: до 90 дней; история входов: 2 года'],
          ['Уведомления в приложении о ваших выводах', 'PayTaxi', 'Сообщения о ходе вывода (договор)', 'Как номер телефона'],
          ['IP-адрес; технические журналы; журнал обращений к Yandex и банкам', 'Ваше устройство; PayTaxi', 'Безопасность, предотвращение мошенничества, устранение сбоев, споры, проверки использования API со стороны Yandex (законный интерес)', 'IP-адрес используется для ограничения попыток входа и в нашей базе данных не хранится; технические журналы (могут содержать IP-адрес, ID и имя) у хостинг-провайдера — {logDays}; журнал обращений — 2 года'],
          ['Пользователи панели администратора (сотрудники парков, оператора и платформы): e-mail, имя, роль, парк, хеш пароля, последний вход, неудачные попытки, выполненные денежные операции и изменения настроек', 'Работодатель (лицо, ответственное за обработку)', 'Безопасный доступ и учёт действий (законный интерес работодателя)', 'Пока есть доступ; запись о действии — пока хранится запись, к которой она относится'],
        ),
        p('По вашей просьбе парк может добавить счёт на имя другого человека, указав причину. Тогда имя и IBAN этого человека обрабатываются, чтобы выплатить вам (законный интерес), а парк обязан передать ему эту политику.'),
        p('Телефон и IBAN — условие сервиса, а не требование закона: без них нельзя войти или вывести деньги. Ни одна из описанных обработок не основана на согласии.'),
        p('Данные с истёкшим сроком удаляются или хранятся в обезличенном виде. Если ваш парк перестанет пользоваться PayTaxi, данные его водителей в течение 30 дней будут возвращены парку и удалены, кроме тех, что закон обязывает хранить.'),
      ],
    },
    {
      id: 'we-do-not',
      title: 'Профилирование, автоматические проверки и cookie',
      blocks: [
        ul(
          'Не продаём данные, не используем их для рекламы или маркетинга и не занимаемся профилированием.',
          'Автоматические проверки отклоняют вывод, если сумма больше вашего баланса в Yandex или лимитов парка либо если ваш аккаунт или парк приостановлен, а также новый счёт для выводов не на ваше имя или в банке, в который парк не платит. Приложение покажет причину; вы можете высказать своё мнение, оспорить отказ и попросить, чтобы вопрос рассмотрел человек.',
          'Не используем cookie и трекеры. В локальном хранилище браузера хранятся только данные для входа, язык и (в панели администратора) последний выбранный парк.',
        ),
      ],
    },
    {
      id: 'recipients',
      title: 'Кто получает ваши данные',
      blocks: [
        p('Помимо вашего парка (он видит только своих водителей) и PayTaxi:'),
        ul(
          'Yandex: каждый вывод — как списание с вашего баланса в Yandex.',
          'Банк вашего парка (сейчас TBC Bank): IBAN получателя, имя владельца, сумма и назначение платежа.',
          'SMS-провайдер ({sms}): ваш номер телефона, код для входа и текст уведомлений о ваших счетах для выплат.',
          'Хостинг-провайдер ({hosting}): хранит данные PayTaxi и технические журналы.',
          'Google: ваш IP-адрес, когда страница загружает шрифт из Google, и письма на адрес {email} (это почтовый ящик Google).',
          'Государственные органы и суды: только в случаях, предусмотренных законодательством Грузии.',
        ),
      ],
    },
    {
      id: 'transfers',
      title: 'Передача данных за пределы Грузии',
      blocks: [
        p('Yandex ({yandexCountry}) и Google (США) получают данные за пределами Грузии, как и наш хостинг-провайдер, если его дата-центр ({hostingLocation}) находится за рубежом. Правовое основание и гарантии каждой передачи: {transferBasis}; копию можно запросить по адресу {email}.'),
      ],
    },
    {
      id: 'security',
      title: 'Как мы защищаем данные',
      blocks: [
        ul(
          'Соединения зашифрованы; на наших серверах номера телефонов, IBAN и ключи доступа парков к банку и Yandex хранятся в зашифрованном виде, а пароли, коды для входа и токены сессий — только в виде хешей.',
          'Число попыток входа ограничено, а аккаунт администратора блокируется после нескольких неверных паролей; сотрудники видят только то, что требует их роль; каждая денежная операция фиксируется.',
          'Если инцидент может затронуть вас, мы незамедлительно сообщим вашему парку, чтобы он уведомил Государственную аудиторскую службу Грузии и, если этого требует закон, вас.',
        ),
      ],
    },
    {
      id: 'rights',
      title: 'Ваши права',
      blocks: [
        p('Как субъект данных вы имеете право:'),
        ul(
          'знать, обрабатываются ли ваши данные и как, и получить их копию;',
          'потребовать их исправить, обновить или дополнить;',
          'потребовать прекратить обработку, удалить, уничтожить или заблокировать данные — в случаях, предусмотренных законом;',
          'получить предоставленные вами данные в машиночитаемом виде или потребовать передать их другому лицу, ответственному за обработку, — в случаях, предусмотренных законом;',
          'не подвергаться исключительно автоматизированному решению, существенно затрагивающему вас, кроме случаев, разрешённых законом;',
          'подать жалобу в Государственную аудиторскую службу Грузии (sao.ge) или в суд.',
        ),
        p('Запросы, направленные нам, мы передаём парку и сначала можем проверить вашу личность. Ответ бесплатный и даётся не позднее 10 рабочих дней (по запросу информации и копии в исключительных случаях срок может быть продлён не более чем на 10 рабочих дней, с указанием причины); решение по запросу о блокировании — не позднее 3 рабочих дней.'),
      ],
    },
    {
      id: 'contact',
      title: 'Изменения, возраст и контакты',
      blocks: [
        p('При каждом изменении обновляются версия и дата вверху страницы; о существенных изменениях мы сообщаем в приложении до их вступления в силу. PayTaxi предназначен только для совершеннолетних (18 лет и старше).'),
        p('Контакты: ваш парк, {email} или почтовый адрес — {provider}, {providerAddress}. У PayTaxi нет офицера по защите персональных данных; если он есть в вашем парке, парк сообщит вам об этом.'),
      ],
    },
  ],
  footer: 'Поставщик PayTaxi — {provider}.',
};

export const PRIVACY_DOCS: Record<LegalLang, PrivacyDoc> = { ka, en, ru };
